// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.DurableTask;
using Models;
using Repositories.Contracts;
using System;
using System.Threading.Tasks;

namespace Hosts.OwnershipReader
{
    public class LoggerFunction
    {
        private readonly ILoggingRepository _loggingRepository;

        public LoggerFunction(ILoggingRepository loggingRepository)
        {
            _loggingRepository = loggingRepository ?? throw new ArgumentNullException(nameof(loggingRepository));
        }

        [FunctionName(nameof(LoggerFunction))]
        public async Task LogMessageAsync([ActivityTrigger] LoggerRequest request)
        {
            await _loggingRepository.LogMessageAsync(
                new LogMessage
                {
                    Message = request.Message,
                    RunId = request.SyncJob?.RunId
                },
                request.Verbosity);
        }
    }
}
