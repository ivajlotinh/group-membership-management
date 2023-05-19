// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

namespace Hosts.JobTrigger
{
    public class GetJobsSegmentedRequest
    {
        public string Query { get; set; }
        public string ContinuationToken { get; set; }
    }
}
