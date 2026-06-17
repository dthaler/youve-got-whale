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
            new CosmosClient(Environment.GetEnvironmentVariable("CosmosDbConnection")));
    })
    .Build();

host.Run();
