// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.
using System;
using System.Threading.Tasks;
using Microsoft.Azure.WebJobs;
using Newtonsoft.Json;
using Repositories.Contracts;
using Repositories.Contracts.InjectConfig;
using Azure.Messaging.ServiceBus;
using Entities;
using System.Text;
using TeamsChannel.Service.Contracts;
using Models;

namespace Hosts.TeamsChannel
{
    public class TeamsChannel
    {
        private readonly ILoggingRepository _loggingRepository;
        private readonly ITeamsChannelService _teamsChannelService;
        private readonly bool _isTeamsChannelDryRunEnabled;

        public TeamsChannel(ILoggingRepository loggingRepository, ITeamsChannelService teamsChannelService, IDryRunValue dryRun)
        {
            _loggingRepository = loggingRepository ?? throw new ArgumentNullException(nameof(loggingRepository));
            _teamsChannelService = teamsChannelService ?? throw new ArgumentNullException(nameof(teamsChannelService));
            _isTeamsChannelDryRunEnabled = dryRun?.DryRunEnabled ?? throw new ArgumentNullException(nameof(dryRun));
        }

        [FunctionName(nameof(TeamsChannel))]
        public async Task RunAsync(
            [ServiceBusTrigger("%serviceBusSyncJobTopic%", "TeamsChannel", Connection = "serviceBusTopicConnection")] ServiceBusReceivedMessage message)
        {
            var syncInfo = GetGroupInfo(message);

            var runId = syncInfo.SyncJob.RunId.GetValueOrDefault(Guid.Empty);
            _loggingRepository.SetSyncJobProperties(runId, syncInfo.SyncJob.ToDictionary());

            await _loggingRepository.LogMessageAsync(new LogMessage { Message = $"TeamsChannel recieved a message. Query: {syncInfo.SyncJob.Query}.", RunId = runId });

            await _loggingRepository.LogMessageAsync(new LogMessage { Message = $"TeamsChannel validating target office group. Query: {syncInfo.SyncJob.Query}.", RunId = runId });

            try
            {

                var parsedAndValidated = await _teamsChannelService.VerifyChannelAsync(syncInfo);

                if (!parsedAndValidated.isGood)
                {
                    await _loggingRepository.LogMessageAsync(new LogMessage { Message = $"Target office group did not validate. Marked as {syncInfo.SyncJob.Status}.", RunId = runId });
                    return;
                }


                var users = await _teamsChannelService.GetUsersFromTeamAsync(parsedAndValidated.parsedChannel, runId);
                await _loggingRepository.LogMessageAsync(new LogMessage { Message = $"Read {users.Count} from {syncInfo.SyncJob.Query}.", RunId = runId });

                // upload to blob storage

                await _loggingRepository.LogMessageAsync(new LogMessage { Message = $"Uploading {users.Count} users from {syncInfo.SyncJob.Query} to blob storage.", RunId = runId });
                var filePath = await _teamsChannelService.UploadMembershipAsync(users, syncInfo, _isTeamsChannelDryRunEnabled);
                await _loggingRepository.LogMessageAsync(new LogMessage { Message = $"Uploaded {users.Count} users from {syncInfo.SyncJob.Query} to blob storage at {filePath}.", RunId = runId });

                // make HTTP call
                await _loggingRepository.LogMessageAsync(new LogMessage { Message = "Making MembershipAggregator request.", RunId = runId });
                await _teamsChannelService.MakeMembershipAggregatorRequestAsync(syncInfo, filePath);
                await _loggingRepository.LogMessageAsync(new LogMessage { Message = "Made MembershipAggregator request.", RunId = runId });
            }
            catch (Exception ex)
            {
                await _loggingRepository.LogMessageAsync(new LogMessage { Message = $"Caught unexpected exception: {ex}. Marking job as errored.", RunId = runId });
                await _teamsChannelService.MarkSyncJobAsErroredAsync(syncInfo.SyncJob);

                // rethrow caught exception so App Insights can get it.
                throw;
            }
            finally
            {
                _loggingRepository.RemoveSyncJobProperties(runId);
            }


            await _loggingRepository.LogMessageAsync(new LogMessage { Message = "TeamsChannel finished.", RunId = runId });
        }

        private ChannelSyncInfo GetGroupInfo(ServiceBusReceivedMessage message)
        {
            return new ChannelSyncInfo
            {
                SyncJob = JsonConvert.DeserializeObject<SyncJob>(Encoding.UTF8.GetString(message.Body)),
                Exclusionary = message.ApplicationProperties.ContainsKey("Exclusionary") ? Convert.ToBoolean(message.ApplicationProperties["Exclusionary"]) : false,
                CurrentPart = message.ApplicationProperties.ContainsKey("CurrentPart") ? Convert.ToInt32(message.ApplicationProperties["CurrentPart"]) : 0,
                TotalParts = message.ApplicationProperties.ContainsKey("TotalParts") ? Convert.ToInt32(message.ApplicationProperties["TotalParts"]) : 0,
                IsDestinationPart = message.ApplicationProperties.ContainsKey("IsDestinationPart") ? Convert.ToBoolean(message.ApplicationProperties["IsDestinationPart"]) : false,
            };
        }


    }
}

