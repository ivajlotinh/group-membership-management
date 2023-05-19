// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

using Microsoft.Graph;
using Microsoft.Kiota.Abstractions.Serialization;
using Polly.Retry;
using Polly;
using Repositories.Contracts;
using System;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using Models;
using Microsoft.Graph.Models;
using System.Collections.Generic;

namespace Repositories.GraphGroups
{
    internal abstract class GraphGroupRepositoryBase
    {
        protected const int MaxResultCount = 999;

        protected readonly ILoggingRepository _loggingRepository;
        protected readonly GraphServiceClient _graphServiceClient;
        protected readonly GraphGroupMetricTracker _graphGroupMetricTracker;

        public GraphGroupRepositoryBase(GraphServiceClient graphServiceClient,
                                        ILoggingRepository loggingRepository,
                                        GraphGroupMetricTracker graphGroupMetricTracker)
        {
            _graphServiceClient = graphServiceClient ?? throw new ArgumentNullException(nameof(graphServiceClient));
            _loggingRepository = loggingRepository ?? throw new ArgumentNullException(nameof(loggingRepository));
            _graphGroupMetricTracker = graphGroupMetricTracker ?? throw new ArgumentNullException(nameof(graphGroupMetricTracker));
        }

        protected AsyncRetryPolicy GetRetryPolicy()
        {
            var retryLimit = 4;
            var timeOutRetryLimit = 2;
            var currentRetryIndex = 0;
            var retryPolicy = Policy.Handle<ServiceException>(ex =>
            {
                if (ex.Message != null
                    && ex.Message.Contains("The request timed out")
                    && currentRetryIndex >= timeOutRetryLimit)
                {
                    return false;
                }

                return true;
            })
                    .WaitAndRetryAsync(
                       retryCount: retryLimit,
                       retryAttempt => TimeSpan.FromMinutes(2),
                       onRetry: async (ex, waitTime, retryIndex, context) =>
                       {
                           currentRetryIndex = retryIndex;

                           var currentLimit = retryLimit;
                           if (ex.Message != null && ex.Message.Contains("The request timed out"))
                           {
                               currentLimit = timeOutRetryLimit;
                           }

                           await _loggingRepository.LogMessageAsync(new LogMessage
                           {
                               Message = $"Got a transient exception. Retrying. This was try {retryIndex} out of {currentLimit}.\n{ex}"
                           });
                       }
                    );

            return retryPolicy;
        }

        protected async Task<T> DeserializeResponseAsync<T>(HttpResponseMessage response, ParsableFactory<T> factory) where T : IParsable
        {
            var rootNode = await GetRootParseNodeAsync(response);
            if (rootNode == null) return default;
            var result = rootNode.GetObjectValue(factory);
            return result;
        }

        protected IEnumerable<AzureADUser> ToUsers(IEnumerable<DirectoryObject> fromGraph, Dictionary<string, int> nonUserGraphObjects)
        {
            if (fromGraph == null)
                yield break;

            foreach (var directoryObj in fromGraph)
            {
                switch (directoryObj)
                {
                    case User user:
                        yield return new AzureADUser { ObjectId = Guid.Parse(user.Id) };
                        break;
                    // We only care about users
                    // I'd prefer to be able to filter these out from the results on Graph's side, but the library doesn't support that yet.
                    // we do want to log the count of non-user graph objects, though
                    default:
                        var dataType = directoryObj.OdataType ?? "Unknown";
                        if (nonUserGraphObjects.TryGetValue(dataType, out int count))
                            nonUserGraphObjects[directoryObj.OdataType] = count + 1;
                        else
                            nonUserGraphObjects[directoryObj.OdataType] = 1;
                        break;
                }
            }
        }

        private async Task<IParseNode> GetRootParseNodeAsync(HttpResponseMessage response)
        {
            var pNodeFactory = ParseNodeFactoryRegistry.DefaultInstance;
            var responseContentType = response.Content?.Headers?.ContentType?.MediaType?.ToLowerInvariant();

            if (string.IsNullOrEmpty(responseContentType))
                return null;

            using var contentStream = await (response.Content?.ReadAsStreamAsync() ?? Task.FromResult(Stream.Null));
            var rootNode = pNodeFactory.GetRootParseNode(responseContentType!, contentStream);
            return rootNode;
        }

    }
}
