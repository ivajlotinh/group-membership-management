// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.DurableTask;
using Models;
using Repositories.Contracts;
using Services.Contracts;
using System;
using System.Threading.Tasks;

namespace Hosts.OwnershipReader
{
    public class UsersSenderFunction
    {
        private readonly ILoggingRepository _loggingRepository;
        private readonly IOwnershipReaderService _ownershipReaderService;

        public UsersSenderFunction(ILoggingRepository loggingRepository, IOwnershipReaderService ownershipReaderService)
        {
            _loggingRepository = loggingRepository ?? throw new ArgumentNullException(nameof(loggingRepository));
            _ownershipReaderService = ownershipReaderService ?? throw new ArgumentNullException(nameof(ownershipReaderService));
        }

        [FunctionName(nameof(UsersSenderFunction))]
        public async Task<string> SendUsersAsync([ActivityTrigger] UsersSenderRequest request)
        {
            await _loggingRepository.LogMessageAsync(new LogMessage { Message = $"{nameof(UsersSenderFunction)} function started", RunId = request.RunId }, VerbosityLevel.DEBUG);
            var filePath = await _ownershipReaderService.SendMembershipAsync(request.SyncJob, request.Users, request.CurrentPart, request.Exclusionary);
            await _loggingRepository.LogMessageAsync(new LogMessage
            {
                RunId = request.RunId,
                Message = $"Successfully uploaded {request.Users.Count} users from source groups {request.SyncJob.Query} to blob storage to be put into the destination group {request.SyncJob.TargetOfficeGroupId}."
            });
            await _loggingRepository.LogMessageAsync(new LogMessage { Message = $"{nameof(UsersSenderFunction)} function completed", RunId = request.RunId }, VerbosityLevel.DEBUG);
            return filePath;
        }
    }
}