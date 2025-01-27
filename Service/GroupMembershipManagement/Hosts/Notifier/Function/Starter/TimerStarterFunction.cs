// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

using System;
using System.Threading.Tasks;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.DurableTask;
using Models;
using Repositories.Contracts;
using Repositories.Contracts.InjectConfig;

namespace Hosts.Notifier
{
    public class TimerStarterFunction
    {
        private readonly ILoggingRepository _loggingRepository = null;
        private readonly IThresholdNotificationConfig _thresholdNotificationConfig;

        public TimerStarterFunction(ILoggingRepository loggingRepository, IThresholdNotificationConfig thresholdNotificationConfig)
        {
            _loggingRepository = loggingRepository ?? throw new ArgumentNullException(nameof(loggingRepository));
            _thresholdNotificationConfig = thresholdNotificationConfig ?? throw new ArgumentNullException(nameof(thresholdNotificationConfig));
        }

        [FunctionName(nameof(TimerStarterFunction))]
        public async Task Run(
            [TimerTrigger("%notifierTriggerSchedule%")] TimerInfo myTimer,
            [DurableClient] IDurableOrchestrationClient starter)
        {
            if (_thresholdNotificationConfig.IsThresholdNotificationEnabled)
            {
                await _loggingRepository.LogMessageAsync(new LogMessage { Message = $"{nameof(TimerStarterFunction)} function started" }, VerbosityLevel.DEBUG);
                var instanceId = await starter.StartNewAsync(nameof(OrchestratorFunction));
                await _loggingRepository.LogMessageAsync(new LogMessage { Message = $"{nameof(TimerStarterFunction)} function completed" }, VerbosityLevel.DEBUG);
            }
        }
    }
}
