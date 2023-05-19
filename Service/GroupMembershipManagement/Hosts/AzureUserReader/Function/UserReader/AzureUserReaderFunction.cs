// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.DurableTask;
using Models;
using Repositories.Contracts;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Hosts.AzureUserReader
{
    public class AzureUserReaderFunction
    {
        private readonly IGraphUserRepository _graphUserRepository = null;
        private readonly ILoggingRepository _loggingRepository = null;

        public AzureUserReaderFunction(IGraphUserRepository graphUserRepository, ILoggingRepository loggingRepository)
        {
            _graphUserRepository = graphUserRepository ?? throw new ArgumentNullException(nameof(graphUserRepository));
            _loggingRepository = loggingRepository ?? throw new ArgumentNullException(nameof(loggingRepository));
        }

        [FunctionName(nameof(AzureUserReaderFunction))]
        public async Task<IList<GraphProfileInformation>> GetUsersAsync([ActivityTrigger] List<string> personnelNumbers)
        {
            await _loggingRepository.LogMessageAsync(new LogMessage { Message = $"{nameof(AzureUserReaderFunction)} function started" }, VerbosityLevel.DEBUG);

            var users = await _graphUserRepository.GetAzureADObjectIdsAsync(personnelNumbers, null);

            await _loggingRepository.LogMessageAsync(new LogMessage { Message = $"{nameof(AzureUserReaderFunction)} function completed" }, VerbosityLevel.DEBUG);

            return users;
        }
    }
}
