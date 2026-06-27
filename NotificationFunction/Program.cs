// SPDX-License-Identifier: MIT
using Microsoft.Azure.Cosmos;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NotificationFunction;
using System;
using System.Net.Http;

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
        services.AddSingleton<HttpClient>();
        services.AddSingleton<CosmosClient>(_ => new CosmosClient(GetRequiredEnvironmentVariable("CosmosDbConnection")));
        services.AddSingleton<IDetectionCounter, CosmosDetectionCounter>();
        services.AddSingleton<INotificationStateStore>(_ =>
            new NotificationStateStore(
                GetRequiredEnvironmentVariable("NotificationCosmosDbConnection"),
                Environment.GetEnvironmentVariable("NotificationCosmosDbDatabase") ?? "orcasound-cosmosdb",
                Environment.GetEnvironmentVariable("NotificationCosmosDbContainer") ?? "Notifications"));
        services.AddSingleton<SendNotification>();
    })
    .Build();

host.Run();
