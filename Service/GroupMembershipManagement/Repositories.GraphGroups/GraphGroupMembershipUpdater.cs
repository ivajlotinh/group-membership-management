// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.
using Microsoft.Graph;
using Models;
using Newtonsoft.Json.Linq;
using Repositories.Contracts;
using Services.Entities;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Repositories.GraphGroups
{
    internal class GraphGroupMembershipUpdater : GraphGroupRepositoryBase
    {
        private const int GraphBatchLimit = 20;
        private const int ConcurrentRequests = 10;

        private static readonly HttpStatusCode[] _shouldRetry = new[]
            { HttpStatusCode.ServiceUnavailable, HttpStatusCode.GatewayTimeout, HttpStatusCode.BadGateway, HttpStatusCode.InternalServerError };
        private static readonly HttpStatusCode[] _isOkay = new[]
            { HttpStatusCode.NoContent, HttpStatusCode.NotFound, HttpStatusCode.OK };

        // These indicate that we're trying to remove a user that's already been removed.
        // Probably because an ID from earlier finally went through between the first try and the retry.
        private static readonly string[] _okayErrorMessages =
            {
                "One or more removed object references do not exist for the following modified properties: 'members'.",
                "One or more added object references already exist for the following modified properties: 'members'."
            };

        private static readonly Regex _userNotFound =
            new Regex(@"Resource '(?<id>[({]?[a-fA-F0-9]{8}[-]?([a-fA-F0-9]{4}[-]?){3}[a-fA-F0-9]{12}[})]?)' does not exist", RegexOptions.IgnoreCase);

        private delegate HttpRequestMessage MakeBulkRequest(List<AzureADUser> batch);

        private List<AzureADUser> _usersNotFound = new List<AzureADUser>();
        private List<AzureADUser> _usersAlreadyExist = new List<AzureADUser>();

        public Guid? RunId { get; set; }

        public GraphGroupMembershipUpdater(GraphServiceClient graphServiceClient,
                                  ILoggingRepository loggingRepository,
                                  GraphGroupMetricTracker graphGroupMetricTracker)
                                  : base(graphServiceClient, loggingRepository, graphGroupMetricTracker)
        { }


        public Task<(ResponseCode ResponseCode, int SuccessCount, List<AzureADUser> UsersNotFound, List<AzureADUser> UsersAlreadyExist)>
            AddUsersToGroup(IEnumerable<AzureADUser> users, AzureADGroup targetGroup)
        {
            //You can, in theory, send batches of 20 requests of 20 group adds each
            // but Graph starts saying "Service Unavailable" for a bunch of them if you do that, so only send so many at once
            // 5 seems to be the most without it starting to throw errors that have to be retried
            return BatchAndSend(users, b => MakeBulkAddRequest(b, targetGroup.ObjectId), GraphBatchLimit, 5);
        }

        private HttpRequestMessage MakeBulkAddRequest(List<AzureADUser> batch, Guid targetGroup)
        {
            return new HttpRequestMessage(HttpMethod.Patch, $"https://graph.microsoft.com/v1.0/groups/{targetGroup}")
            {
                Content = new StringContent(MakeAddRequestBody(batch), System.Text.Encoding.UTF8, "application/json"),
            };
        }

        private static string MakeAddRequestBody(List<AzureADUser> users)
        {
            JObject body = new JObject
            {
                ["members@odata.bind"] = JArray.FromObject(users.Select(x => $"https://graph.microsoft.com/v1.0/users/{x.ObjectId}"))
            };
            return body.ToString(Newtonsoft.Json.Formatting.None);
        }

        public Task<(ResponseCode ResponseCode, int SuccessCount, List<AzureADUser> UsersNotFound, List<AzureADUser> UsersAlreadyExist)>
            RemoveUsersFromGroup(IEnumerable<AzureADUser> users, AzureADGroup targetGroup)
        {
            // This, however, is the most we can send per delete batch, and it works pretty well.
            return BatchAndSend(users, b => MakeBulkRemoveRequest(b, targetGroup.ObjectId), 1, GraphBatchLimit);
        }

        private HttpRequestMessage MakeBulkRemoveRequest(List<AzureADUser> batch, Guid targetGroup)
        {
            // You have to remove users with their object ID. UPN won't work because you can only use it when the thing you're removing is
            // unambiguously a user.

            if (batch.Count != 1) { throw new ArgumentException("Batches of deletes must have exactly one item. This one has " + batch.Count); }

            var toRemove = batch.Single().ObjectId;
            return new HttpRequestMessage(HttpMethod.Delete, $"https://graph.microsoft.com/v1.0/groups/{targetGroup}/members/{toRemove}/$ref");
        }

        private string GetNewChunkId() => $"{Guid.NewGuid().ToString().Replace("-", string.Empty)}-";

        private async Task<(ResponseCode ResponseCode, int SuccessCount, List<AzureADUser> UsersNotFound, List<AzureADUser> UsersAlreadyExist)>
            BatchAndSend(IEnumerable<AzureADUser> users, MakeBulkRequest makeRequest, int requestMax, int batchSize)
        {
            if (!users.Any()) { return (ResponseCode.Ok, 0, new List<AzureADUser>(), new List<AzureADUser>()); }

            var queuedBatches = new ConcurrentQueue<ChunkOfUsers>(
                    users.Chunk(requestMax) // Chop up the users into chunks of how many per graph request (20 for add, 1 for remove)
                    .Select(x => new ChunkOfUsers
                    {
                        ToSend = x.ToList(),
                        Id = x[0].MembershipAction == MembershipAction.Add ? GetNewChunkId() : x[0].ObjectId.ToString()
                    }));

            var responses = await Task.WhenAll(Enumerable.Range(0, ConcurrentRequests).Select(x => ProcessQueue(queuedBatches, makeRequest, x, batchSize)));

            var status = responses.Any(x => x.ResponseCode == ResponseCode.Error) ? ResponseCode.Error : ResponseCode.Ok;
            return (status, responses.Sum(x => x.SuccessCount), _usersNotFound, _usersAlreadyExist);
        }

        private async Task<(ResponseCode ResponseCode, int SuccessCount)> ProcessQueue(ConcurrentQueue<ChunkOfUsers> queue, MakeBulkRequest makeRequest, int threadNumber, int batchSize)
        {
            var successCount = 0;

            do
            {
                var toSend = new List<ChunkOfUsers>();
                while (queue.TryDequeue(out var step))
                {
                    toSend.Add(step);
                    if (toSend.Count == batchSize)
                    {
                        var response = await ProcessBatch(queue, toSend, makeRequest, threadNumber);
                        toSend.Clear();

                        successCount += response.SuccessCount;

                        if (response.ResponseCode == ResponseCode.Error)
                            return response;
                    }
                }

                if (toSend.Any())
                {
                    var response = await ProcessBatch(queue, toSend, makeRequest, threadNumber);

                    successCount += response.SuccessCount;

                    if (response.ResponseCode == ResponseCode.Error)
                        return response;
                }

            } while (!queue.IsEmpty); // basically, that last ProcessBatch may have put more stuff in the queue

            return (ResponseCode.Ok, successCount);
        }

        private async Task<(ResponseCode ResponseCode, int SuccessCount)> ProcessBatch(ConcurrentQueue<ChunkOfUsers> queue, List<ChunkOfUsers> toSend, MakeBulkRequest makeRequest, int threadNumber)
        {
            await _loggingRepository.LogMessageAsync(new LogMessage { Message = $"Thread number {threadNumber}: Sending a batch of {toSend.Count} requests.", RunId = RunId }, VerbosityLevel.DEBUG);
            int requeued = 0;
            bool hasUnrecoverableErrors = false;
            var successfulRequests = toSend.SelectMany(x => x.ToSend).ToList().Count;

            try
            {
                var batchRequestSteps = toSend.Select(x => new BatchRequestStep(x.Id, makeRequest(x.ToSend))).ToArray();
                var batchRequestContent = new BatchRequestContent(_graphServiceClient, batchRequestSteps);

                await foreach (var idToRetry in await SendBatch(batchRequestContent))
                {
                    var chunkToRetry = toSend.First(x => x.Id == idToRetry.RequestId);

                    successfulRequests -= chunkToRetry.ToSend.Count;

                    if (idToRetry.ResponseCode == ResponseCode.Error)
                    {
                        hasUnrecoverableErrors = true;
                        break;
                    }

                    if (chunkToRetry.ShouldRetry)
                    {
                        // Not found
                        if (!string.IsNullOrWhiteSpace(idToRetry.AzureObjectId))
                        {
                            var notFoundUser = chunkToRetry.ToSend.FirstOrDefault(x => x.ObjectId.ToString().Equals(idToRetry.AzureObjectId, StringComparison.InvariantCultureIgnoreCase));
                            if (notFoundUser != null)
                            {
                                chunkToRetry.ToSend.Remove(notFoundUser);
                            }

                            if (chunkToRetry.ToSend.Count == 1 && chunkToRetry.ToSend[0].MembershipAction == MembershipAction.Remove)
                            {
                                continue;
                            }
                        }

                        // Break down request for individual retries
                        if (chunkToRetry.ToSend.Count > 1 && idToRetry.ResponseCode == ResponseCode.IndividualRetry)
                        {
                            var chunksOfUsers = chunkToRetry.ToSend.Select(x => new ChunkOfUsers
                            {
                                Id = x.ObjectId.ToString(),
                                ToSend = new List<AzureADUser> { x }
                            });

                            foreach (var chunk in chunksOfUsers)
                            {
                                requeued++;
                                queue.Enqueue(chunk.UpdateIdForRetry(threadNumber));
                                await _loggingRepository.LogMessageAsync(new LogMessage { Message = $"Queued {chunk.Id} from {chunkToRetry.Id}", RunId = RunId });
                            }

                            chunkToRetry.ToSend.Clear();
                        }

                        if (chunkToRetry.ToSend.Count > 0)
                        {
                            if (chunkToRetry.ToSend.Count == 1 && idToRetry.ResponseCode == ResponseCode.Ok && idToRetry.HttpStatusCode == HttpStatusCode.BadRequest)
                            {
                                await _loggingRepository.LogMessageAsync(new LogMessage
                                {
                                    Message = $"{chunkToRetry.Id} already exists",
                                    RunId = RunId
                                });

                                _usersAlreadyExist.Add(chunkToRetry.ToSend[0]);
                            }

                            else
                            {
                                requeued++;
                                var originalId = chunkToRetry.Id;
                                queue.Enqueue(chunkToRetry.UpdateIdForRetry(threadNumber));
                                await _loggingRepository.LogMessageAsync(new LogMessage { Message = $"Requeued {originalId} as {chunkToRetry.Id}", RunId = RunId });
                            }
                        }
                    }
                }
                await _loggingRepository.LogMessageAsync(new LogMessage { Message = $"Thread number {threadNumber}: {toSend.Count - requeued} out of {toSend.Count} requests succeeded. {queue.Count} left.", RunId = RunId }, VerbosityLevel.DEBUG);
            }
            catch (ServiceException ex)
            {
                // winding up in here is a pretty rare event
                // Usually, it's because either a timeout happened or something else weird went on
                // the best thing to do is just requeue the chunks
                // but if a chunk has already been queued five times or so, drop it on the floor so we don't go forever
                // in the future, log the exception and which ones get dropped.

                await _loggingRepository.LogMessageAsync(new LogMessage
                {
                    Message = ex.GetBaseException().ToString(),
                    RunId = RunId
                });

                foreach (var chunk in toSend)
                {
                    if (chunk.ShouldRetry)
                    {
                        var originalId = chunk.Id;
                        queue.Enqueue(chunk.UpdateIdForRetry(threadNumber));

                        await _loggingRepository.LogMessageAsync(new LogMessage { Message = $"Requeued {originalId} as {chunk.Id}", RunId = RunId });
                    }
                }
            }

            var status = hasUnrecoverableErrors ? ResponseCode.Error : ResponseCode.Ok;
            return (status, successfulRequests);
        }

        private async Task<IAsyncEnumerable<RetryResponse>> SendBatch(BatchRequestContent tosend)
        {
            try
            {
                await _loggingRepository.LogMessageAsync(new LogMessage { Message = $"Sending requests {string.Join(",", tosend.BatchRequestSteps.Keys)}.", RunId = RunId });

                var response = await _graphServiceClient.Batch.PostAsync(tosend);
                return GetStepIdsToRetry(await response.GetResponsesAsync(), (Dictionary<string, BatchRequestStep>)tosend.BatchRequestSteps);
            }
            catch (ServiceException ex)
            {
                await _loggingRepository.LogMessageAsync(new LogMessage
                {
                    Message = ex.GetBaseException().ToString(),
                    RunId = RunId
                });

                throw;
            }
        }

        private static TimeSpan CalculateThrottleWait(RetryConditionHeaderValue wait)
        {
            // we're much more likely to hit the write quota, so default to the 2 minute and 30 second wait
            // https://docs.microsoft.com/en-us/graph/throttling#pattern
            TimeSpan waitFor = TimeSpan.FromSeconds(150);
            if (wait.Delta.HasValue) { waitFor = wait.Delta.Value; }
            if (wait.Date.HasValue) { waitFor = wait.Date.Value - DateTimeOffset.UtcNow; }
            return waitFor;
        }

        private static bool IsOkayError(string error)
        {
            error = JObject.Parse(error)["error"]["message"].Value<string>();
            return _okayErrorMessages.Any(x => error.Contains(x));
        }

        private async IAsyncEnumerable<RetryResponse> GetStepIdsToRetry(
            Dictionary<string, HttpResponseMessage> responses, Dictionary<string, BatchRequestStep> requests)
        {
            bool beenThrottled = false;

            var resourceUnitsUsed = _graphGroupMetricTracker.GetMetric(nameof(Metric.ResourceUnitsUsed));
            var throttleLimitPercentage = _graphGroupMetricTracker.GetMetric(nameof(Metric.ThrottleLimitPercentage));
            var writesUsed = _graphGroupMetricTracker.GetMetric(nameof(Metric.WritesUsed));

            foreach (var kvp in responses)
            {
                //Ensure that the response messages get disposed of.
                using var response = kvp.Value;
                var status = response.StatusCode;
                var content = await response.Content.ReadAsStringAsync();

                if (response.Headers.TryGetValues(GraphResponseHeader.ResourceUnitHeader, out var resourceValues))
                {
                    int ruu = GraphGroupMetricTracker.ParseFirst<int>(resourceValues, int.TryParse);
                    await _loggingRepository.LogMessageAsync(new LogMessage { Message = $"Resource unit cost of {Enum.GetName(typeof(QueryType), QueryType.Other)} - {ruu}", RunId = RunId });
                    _graphGroupMetricTracker.TrackResourceUnitsUsedByTypeEvent(ruu, QueryType.Other, RunId);
                    resourceUnitsUsed.TrackValue(ruu);
                }

                if (response.Headers.TryGetValues(GraphResponseHeader.ThrottlePercentageHeader, out var throttleValues))
                    throttleLimitPercentage.TrackValue(GraphGroupMetricTracker.ParseFirst<double>(throttleValues, double.TryParse));

                await _loggingRepository.LogMessageAsync(new LogMessage
                {
                    Message = $"Response - RequestId:{kvp.Key} - StatusCode:{status} - Content:{content}",
                    RunId = RunId
                }, VerbosityLevel.DEBUG);


                // Note that the ones with empty bodies mean "this response is okay and we don't have to do anything about it."
                if (status == HttpStatusCode.BadRequest && IsOkayError(content))
                {
                    yield return new RetryResponse
                    {
                        RequestId = kvp.Key,
                        ResponseCode = ResponseCode.Ok,
                        HttpStatusCode = HttpStatusCode.BadRequest
                    };
                }
                else if (status == HttpStatusCode.NotFound && (content).Contains("does not exist or one of its queried reference-property objects are not present."))
                {
                    var match = _userNotFound.Match(content);
                    var userId = default(string);

                    if (match.Success)
                    {
                        userId = match.Groups["id"].Value;
                        await _loggingRepository.LogMessageAsync(new LogMessage
                        {
                            Message = $"User ID is found",
                            RunId = RunId
                        });

                        var requestStep = requests[kvp.Key];

                        if (requestStep.Request.Method == HttpMethod.Delete)
                        {
                            await _loggingRepository.LogMessageAsync(new LogMessage
                            {
                                Message = $"Removing {requestStep.RequestId} failed as this resource does not exists.",
                                RunId = RunId
                            });

                            _usersNotFound.Add(new AzureADUser { ObjectId = Guid.Parse(requestStep.RequestId) });
                        }
                        else
                        {
                            await _loggingRepository.LogMessageAsync(new LogMessage
                            {
                                Message = $"Adding {userId} failed as this resource does not exists.",
                                RunId = RunId
                            });

                            _usersNotFound.Add(new AzureADUser { ObjectId = Guid.Parse(userId) });
                        }
                    }

                    else
                    {
                        await _loggingRepository.LogMessageAsync(new LogMessage
                        {
                            Message = $"User ID is missing",
                            RunId = RunId
                        });

                        yield return new RetryResponse
                        {
                            RequestId = kvp.Key,
                            ResponseCode = ResponseCode.IndividualRetry
                        };
                    }

                    yield return new RetryResponse
                    {
                        RequestId = kvp.Key,
                        ResponseCode = ResponseCode.IndividualRetry,
                        AzureObjectId = userId
                    };

                }
                else if (_isOkay.Contains(status)) { writesUsed.TrackValue(1); }
                else if (status == HttpStatusCode.TooManyRequests)
                {
                    // basically, each request in the batch will probably say it's been throttled
                    // but we only need to wait the first time.
                    // this isn't strictly true- i believe that the count gets reset if any other threads send requests
                    // but it's true enough until we can engineer something more robust

                    if (!beenThrottled)
                    {
                        // basically, go ahead and start waiting while we log the throttling info
                        // add a few seconds to account for other 419s that happen before we can send the signal to pause.
                        var throttleWait = CalculateThrottleWait(response.Headers.RetryAfter) + TimeSpan.FromSeconds(10);

                        var startThrottling = Task.Delay(throttleWait);
                        var gotThrottleInfo = response.Headers.TryGetValues(GraphResponseHeader.ThrottleInfoHeader, out var throttleInfo);
                        var gotThrottleScope = response.Headers.TryGetValues(GraphResponseHeader.ThrottleScopeHeader, out var throttleScope);
                        await _loggingRepository.LogMessageAsync(new LogMessage
                        {
                            Message = string.Format("Got 429 throttled. Waiting {0} seconds. Delta: {1} Date: {2} Reason: {3} Scope: {4}",
                                throttleWait.TotalSeconds,
                                response.Headers.RetryAfter.Delta != null ? response.Headers.RetryAfter.Delta.ToString() : "(none)",
                                response.Headers.RetryAfter.Date != null ? response.Headers.RetryAfter.Date.ToString() : "(none)",
                                gotThrottleInfo ? string.Join(',', throttleInfo) : "(none)",
                                gotThrottleScope ? string.Join(',', throttleScope) : "(none)"),
                            RunId = RunId
                        });
                        await startThrottling;
                        beenThrottled = true;
                    }

                    // it's possible for only some requests in a batch to be throttled, so only retry the ones that were throttled.
                    yield return new RetryResponse
                    {
                        RequestId = kvp.Key,
                        ResponseCode = ResponseCode.IndividualRetry
                    };
                }
                else if (status == HttpStatusCode.Forbidden && content.Contains("Guests users are not allowed to join"))
                {
                    yield return new RetryResponse
                    {
                        RequestId = kvp.Key,
                        ResponseCode = ResponseCode.IndividualRetry
                    };
                }
                else if (_shouldRetry.Contains(status))
                {
                    yield return new RetryResponse
                    {
                        RequestId = kvp.Key,
                        ResponseCode = ResponseCode.Ok
                    };
                }
                else
                {
                    await _loggingRepository.LogMessageAsync(new LogMessage { Message = $"Got an unexpected error from Graph, stopping all processing for current job: {status} {response.ReasonPhrase} {content}.", RunId = RunId });
                    yield return new RetryResponse
                    {
                        RequestId = kvp.Key,
                        ResponseCode = ResponseCode.Error
                    };
                }
            }
        }


        private class ChunkOfUsers
        {
            public List<AzureADUser> ToSend { get; set; }
            public string Id { get; set; }

            private const int MaxBatchRetries = 5;

            // basically, whenever a batch is retried, we append the thread number after a dash
            public bool ShouldRetry => Id.Split('-')[1].Length < MaxBatchRetries;
            public ChunkOfUsers UpdateIdForRetry(int threadNumber)
            {
                Id += threadNumber;
                return this;
            }
        }

        private class RetryResponse
        {
            public string RequestId { get; set; }
            public ResponseCode ResponseCode { get; set; }
            public HttpStatusCode HttpStatusCode { get; set; }
            public string AzureObjectId { get; set; }
        }
    }
}
