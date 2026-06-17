// SPDX-License-Identifier: MIT
using Amazon;
using Amazon.SimpleEmail;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System;

var host = new HostBuilder()
    .ConfigureFunctionsWorkerDefaults()
    .ConfigureServices(services =>
    {
        services.AddLogging();
        services.AddSingleton<IAmazonSimpleEmailService>(_ =>
        {
            string regionName = Environment.GetEnvironmentVariable("AwsRegion") ?? "us-west-2";
            var region = RegionEndpoint.GetBySystemName(regionName);
            return new AmazonSimpleEmailServiceClient(region);
        });
        services.AddSingleton<CosmosClient>(_ =>
        {
            string? connectionString = Environment.GetEnvironmentVariable("CosmosDbConnection");
            if (string.IsNullOrWhiteSpace(connectionString))
            {
                throw new InvalidOperationException("CosmosDbConnection environment variable is not configured");
            }

            return new CosmosClient(connectionString);
        });
    })
    .Build();

host.Run();
