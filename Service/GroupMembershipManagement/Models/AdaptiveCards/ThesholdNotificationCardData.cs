// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

namespace Models.AdaptiveCards
{
    public class ThesholdNotificationCardData
    {
        public string GroupName { get; set; }
        public int ChangeQuantityForAdditions { get; set; }
        public int ChangeQuantityForRemovals { get; set; }
        public int ChangePercentageForAdditions { get; set; }
        public int ChangePercentageForRemovals { get; set; }
        public int ThresholdPercentageForAdditions { get; set; }
        public int ThresholdPercentageForRemovals { get; set; }
        public string NotificationId { get; set; }
        public string ApiHostname { get; set; }
        public string ProviderId { get; set; }
    }
}