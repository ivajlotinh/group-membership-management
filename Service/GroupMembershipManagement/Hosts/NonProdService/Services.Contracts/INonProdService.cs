// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.
using Entities;
using Models.Entities;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Services.Contracts
{
    public interface INonProdService
    {
        MembershipDifference GetMembershipDifference(List<AzureADUser> currentMembership, List<AzureADUser> targetMembership);
    }
}
