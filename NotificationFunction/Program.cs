// SPDX-License-Identifier: MIT
using Amazon;
using Amazon.SimpleEmail;
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NotificationFunction;
using System;

string GetRequiredEnvironmentVariable(string name)
{
    string? value = Environment.GetEnvironmentVariable(name);
    if (string.IsNullOrWhiteSpace(value))
    {
        throw new InvalidOperationException($"{name} environment variable is not configured");
    }

    return value;
}

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
        services.AddSingleton<CosmosClient>(_ => new CosmosClient(GetRequiredEnvironmentVariable("CosmosDbConnection")));
        services.AddSingleton<NotificationStateStore>(_ =>
            new NotificationStateStore(
                GetRequiredEnvironmentVariable("NotificationCosmosDbConnection"),
                Environment.GetEnvironmentVariable("NotificationCosmosDbDatabase") ?? "orcasound-cosmosdb",
                Environment.GetEnvironmentVariable("NotificationCosmosDbContainer") ?? "Notifications"));
    })
    .Build();

host.Run();
