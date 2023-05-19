// Copyright (c) Microsoft Corporation.
// Licensed under the MIT license.

using Common.DependencyInjection;
using DIConcreteTypes;
using Hosts.FunctionBase;
using Microsoft.Azure.Functions.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Graph;
using Repositories.Contracts;
using Repositories.Contracts.InjectConfig;
using Repositories.GraphAzureADUsers;
using Services;
using Services.Contracts;

[assembly: FunctionsStartup(typeof(Hosts.AzureUserReader.Startup))]

namespace Hosts.AzureUserReader
{
    public class Startup : CommonStartup
    {
        protected override string FunctionName => nameof(AzureUserReader);
        protected override string DryRunSettingName => string.Empty;

        public override void Configure(IFunctionsHostBuilder builder)
        {
            base.Configure(builder);

            builder.Services.AddSingleton(services =>
                new GraphServiceClient(FunctionAppDI.CreateAuthenticationProvider(services.GetService<IOptions<GraphCredentials>>().Value)));

            builder.Services.AddSingleton<IGraphServiceAttemptsValue>(services =>
            {
                var parseMaxRetryAfterAttempts = int.TryParse(GetValueOrThrow("maxRetryAfterAttempts"), out int maxRetryAfterAttempts);
                var parseMaxExceptionHandlingAttempts = int.TryParse(GetValueOrThrow("maxExceptionHandlingAttempts"), out int maxExceptionHandlingAttempts);
                if (!parseMaxRetryAfterAttempts || !parseMaxExceptionHandlingAttempts)
                {
                    maxRetryAfterAttempts = 4;
                    maxExceptionHandlingAttempts = 2;
                }

                return new GraphServiceAttemptsValue(maxRetryAfterAttempts, maxExceptionHandlingAttempts);
            });

            builder.Services.AddSingleton<IStorageAccountSecret>(services =>
                new StorageAccountSecret(GetValueOrThrow("storageAccountConnectionString")));

            builder.Services.AddScoped<IGraphUserRepository, GraphUserRepository>();
            builder.Services.AddScoped<IBlobClientFactory, BlobClientFactory>();
            builder.Services.AddScoped<IAzureUserReaderService, AzureUserReaderService>();
        }
    }
}