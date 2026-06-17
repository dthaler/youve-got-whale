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
            new AmazonSimpleEmailServiceClient(RegionEndpoint.USWest2));
        services.AddSingleton<CosmosClient>(_ =>
            new CosmosClient(Environment.GetEnvironmentVariable("CosmosDbConnection")));
    })
    .Build();

host.Run();
