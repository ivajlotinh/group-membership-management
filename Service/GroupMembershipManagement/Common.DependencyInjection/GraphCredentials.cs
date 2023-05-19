// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.
namespace Common.DependencyInjection
{
    public class GraphCredentials
    {
        public string TenantId { get; set; }
        public string ClientId { get; set; }
        public string ClientSecret { get; set; }
        public string ClientCertificateName { get; set; }
        public string RedirectURI { get; set; }
        public string KeyVaultName { get; set; }
        public string KeyVaultTenantId { get; set; }
        public string EmailSenderUserName { get; set; }
        public string EmailSenderPassword { get; set; }
    }
}
