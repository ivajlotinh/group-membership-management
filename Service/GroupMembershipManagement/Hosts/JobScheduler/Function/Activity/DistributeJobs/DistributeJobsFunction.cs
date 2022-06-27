// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

using Entities;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.DurableTask;
using System;
using System.Threading.Tasks;
using Services.Contracts;
using Repositories.Contracts;

namespace Hosts.JobScheduler
{
    public class DistributeJobsFunction
    {
        private readonly IJobSchedulingService _jobSchedulingService = null;
        private readonly ILoggingRepository _loggingRepository = null;
        public DistributeJobsFunction(IJobSchedulingService jobSchedulingService, ILoggingRepository loggingRepository)
        {
            _loggingRepository = loggingRepository ?? throw new ArgumentNullException(nameof(loggingRepository));
            _jobSchedulingService = jobSchedulingService ?? throw new ArgumentNullException(nameof(jobSchedulingService));
        }

        [FunctionName(nameof(DistributeJobsFunction))]
        public async Task DistributeJobs([ActivityTrigger] DistributeJobsRequest request)
        {
            await _loggingRepository.LogMessageAsync(new LogMessage { Message = $"{nameof(DistributeJobsFunction)} function started at: {DateTime.UtcNow}" }, VerbosityLevel.DEBUG);
            await _jobSchedulingService.DistributeJobs(request.JobsToDistribute);
            await _loggingRepository.LogMessageAsync(new LogMessage { Message = $"{nameof(DistributeJobsFunction)} function completed at: {DateTime.UtcNow}" }, VerbosityLevel.DEBUG);
        }
    }
}
