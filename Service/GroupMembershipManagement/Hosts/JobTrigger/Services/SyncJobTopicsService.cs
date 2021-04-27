// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.
using Entities;
using Repositories.Contracts;
using Repositories.Contracts.InjectConfig;
using Services.Contracts;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Services
{
    public class SyncJobTopicsService : ISyncJobTopicService
    {
        private const string EmailSubject = "EmailSubject";
        private const string SyncStartedEmailBody = "SyncStartedEmailBody";
        private const string SyncDisabledNoGroupEmailBody = "SyncDisabledNoGroupEmailBody";
        private const string SyncDisabledNoOwnerEmailBody = "SyncDisabledNoOwnerEmailBody";

        private readonly ILoggingRepository _loggingRepository;
        private readonly ISyncJobRepository _syncJobRepository;
        private readonly IServiceBusTopicsRepository _serviceBusTopicsRepository;
        private readonly IGraphGroupRepository _graphGroupRepository;
        private readonly string _gmmAppId;
        private readonly IMailRepository _mailRepository;
        private readonly IEmailSenderRecipient _emailSenderAndRecipients;

        public SyncJobTopicsService(
            ILoggingRepository loggingRepository,
            ISyncJobRepository syncJobRepository,
            IServiceBusTopicsRepository serviceBusTopicsRepository,
            IGraphGroupRepository graphGroupRepository,
            IKeyVaultSecret<ISyncJobTopicService> gmmAppId,
            IMailRepository mailRepository,
            IEmailSenderRecipient emailSenderAndRecipients
            )
        {
            _emailSenderAndRecipients = emailSenderAndRecipients;
            _loggingRepository = loggingRepository ?? throw new ArgumentNullException(nameof(loggingRepository));
            _syncJobRepository = syncJobRepository ?? throw new ArgumentNullException(nameof(syncJobRepository));
            _serviceBusTopicsRepository = serviceBusTopicsRepository ?? throw new ArgumentNullException(nameof(serviceBusTopicsRepository));
            _graphGroupRepository = graphGroupRepository ?? throw new ArgumentNullException(nameof(graphGroupRepository));
            _gmmAppId = gmmAppId.Secret;
            _mailRepository = mailRepository ?? throw new ArgumentNullException(nameof(mailRepository));
        }

        public async Task ProcessSyncJobsAsync()
        {
            var jobs = _syncJobRepository.GetSyncJobsAsync(SyncStatus.Idle);

            var runningJobs = new List<SyncJob>();
            var failedJobs = new List<SyncJob>();
            var startedTasks = new List<Task>();
            await foreach (var job in jobs)
            {
                job.RunId = _graphGroupRepository.RunId = Guid.NewGuid();
                _loggingRepository.SyncJobProperties = job.ToDictionary();

                var groupName = await _graphGroupRepository.GetGroupNameAsync(job.TargetOfficeGroupId);

                if (job.LastRunTime == DateTime.FromFileTimeUtc(0))
                {
                    var message = new EmailMessage
                    {
                        Subject = EmailSubject,
                        Content = SyncStartedEmailBody,
                        SenderAddress = _emailSenderAndRecipients.SenderAddress,
                        SenderPassword = _emailSenderAndRecipients.SenderPassword,
                        ToEmailAddresses = job.Requestor,
                        CcEmailAddresses = string.Empty,
                        AdditionalContentParams = new[] { groupName, job.TargetOfficeGroupId.ToString() }
                    };

                    await SendEmailAsync(message, job.RunId);
                }

                if (await CanWriteToGroup(job))
                {
                    await _loggingRepository.LogMessageAsync(new LogMessage
                    {
                        RunId = job.RunId,
                        Message = $"Starting job."
                    });
                    runningJobs.Add(job);
                }
                else
                {
                    job.Enabled = false;
                    failedJobs.Add(job);
                }

                // Don't leak this to the start and stop logs.
                // The logging repository has this SyncJobInfo property that gets appended to all the logs,
                // to make it easier to log information like the run ID and so on without having to pass all that around.
                // However, the same logging repository gets reused for the life of the program, which means that, without this line,
                // it'll append that information to the logs that say "JobTrigger function started" and "JobTrigger function completed".

                _loggingRepository.SyncJobProperties = null;
            }

            startedTasks.Add(_syncJobRepository.UpdateSyncJobStatusAsync(runningJobs, SyncStatus.InProgress));
            startedTasks.Add(_syncJobRepository.UpdateSyncJobStatusAsync(failedJobs, SyncStatus.Error));
            await Task.WhenAll(startedTasks);

            runningJobs.ForEach(async job => await _serviceBusTopicsRepository.AddMessageAsync(job));

            foreach (var failedJob in failedJobs)
            {
                var message = new EmailMessage
                {
                    Subject = EmailSubject,
                    Content = SyncDisabledEmailBody,
                    SenderAddress = _emailSenderAndRecipients.SenderAddress,
                    SenderPassword = _emailSenderAndRecipients.SenderPassword,
                    ToEmailAddresses = failedJob.Requestor,
                    CcEmailAddresses = _emailSenderAndRecipients.SyncDisabledCCAddresses,
                    AdditionalContentParams = new[] { failedJob.TargetOfficeGroupId.ToString() }
                };

                await SendEmailAsync(message, failedJob.RunId);
            }
        }

        private async Task SendEmailAsync(EmailMessage message, Guid? runId)
        {
            try
            {
                await _mailRepository.SendMailAsync(message);
            }
            catch (Microsoft.Graph.ServiceException ex) when (ex.GetBaseException().GetType().Name == "MsalUiRequiredException")
            {
                await _loggingRepository.LogMessageAsync(new LogMessage
                {
                    RunId = runId,
                    Message = "Email cannot be sent because Mail.Send permission has not been granted."
                });
            }
            catch (Microsoft.Graph.ServiceException ex) when (ex.Message.Contains("MailboxNotEnabledForRESTAPI"))
            {
                await _loggingRepository.LogMessageAsync(new LogMessage
                {
                    RunId = runId,
                    Message = "Email cannot be sent because required licenses are missing in the service account."
                });
            }
            catch (Exception ex)
            {
                await _loggingRepository.LogMessageAsync(new LogMessage
                {
                    RunId = runId,
                    Message = $"Email cannot be sent due to an unexpected exception.\n{ex}"
                });
            }
        }

        private async Task<bool> CanWriteToGroup(SyncJob job)
        {
            foreach (var strat in new JobVerificationStrategy[] {
                new JobVerificationStrategy { TestFunction = _graphGroupRepository.GroupExists, StatusMessage = $"Destination group {job.TargetOfficeGroupId} exists.", ErrorMessage = $"destination group {job.TargetOfficeGroupId} doesn't exist.", EmailBody = SyncDisabledNoGroupEmailBody },
                new JobVerificationStrategy { TestFunction = (groupId) => _graphGroupRepository.IsAppIDOwnerOfGroup(_gmmAppId, groupId), StatusMessage = $"GMM is an owner of destination group {job.TargetOfficeGroupId}.", ErrorMessage = $"GMM is not an owner of destination group {job.TargetOfficeGroupId}.", EmailBody = SyncDisabledNoOwnerEmailBody }})
            {
                await _loggingRepository.LogMessageAsync(new LogMessage { RunId = job.RunId, Message = "Checking: " + strat.StatusMessage });
                // right now, we stop after the first failed strategy, because it doesn't make sense to find that the destination group doesn't exist and then check if we own it.
                // this can change in the future, when/if we have more than two things to check here.
                if (await strat.TestFunction(job.TargetOfficeGroupId) == false)
                {
                    await _loggingRepository.LogMessageAsync(new LogMessage { RunId = job.RunId, Message = "Marking sync job as failed because " + strat.ErrorMessage });
					await _mailRepository.SendMailAsync(
					new EmailMessage
					{
						Subject = EmailSubject,
						Content = strat.EmailBody,
						SenderAddress = _emailSenderAndRecipients.SenderAddress,
						SenderPassword = _emailSenderAndRecipients.SenderPassword,
						ToEmailAddresses = job.Requestor,
						CcEmailAddresses = _emailSenderAndRecipients.SyncDisabledCCAddresses,
						AdditionalContentParams = new[] { job.TargetOfficeGroupId.ToString() }
					}
						);
                    return false;
                }

                await _loggingRepository.LogMessageAsync(new LogMessage { RunId = job.RunId, Message = "Check passed: " + strat.StatusMessage });
            }

            return true;
        }

        private class JobVerificationStrategy
        {
            public delegate Task<bool> CanWriteToGroup(Guid groupId);

            public CanWriteToGroup TestFunction { get; set; }
            public string StatusMessage { get; set; }
            public string ErrorMessage { get; set; }
            public string EmailBody { get; set; }
        }
    }
}
