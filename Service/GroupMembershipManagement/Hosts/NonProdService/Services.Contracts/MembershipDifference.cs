// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.
using Models;
using System.Collections.Generic;

namespace Services
{
    public class MembershipDifference
    {
        public List<AzureADUser> UsersToAdd { get; set; }
        public List<AzureADUser> UsersToRemove { get; set; }
    }
}
