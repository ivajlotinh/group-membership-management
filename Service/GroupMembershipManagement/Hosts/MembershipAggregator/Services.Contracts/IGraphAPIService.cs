// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.
using Models;
using Polly;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Services.Contracts
{
    public interface IGraphAPIService
    {
        public Guid RunId { get; set; }
        public Task<string> GetGroupNameAsync(Guid groupId);
        public Task<PolicyResult<bool>> GroupExistsAsync(Guid groupId, Guid runId);
        public Task<List<AzureADUser>> GetGroupOwnersAsync(Guid groupObjectId, int top = 0);
        public Task<bool> IsEmailRecipientOwnerOfGroupAsync(string email, Guid groupObjectId);
        public Task SendEmailAsync(string toEmail, string contentTemplate, string[] additionalContentParams, Guid runId, string ccEmail = null, string emailSubject = null, string[] additionalSubjectParams = null);
    }
}