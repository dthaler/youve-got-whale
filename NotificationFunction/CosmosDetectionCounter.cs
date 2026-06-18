// SPDX-License-Identifier: MIT
using Microsoft.Azure.Cosmos;
using System;
using System.Text.Json;
using System.Threading.Tasks;

namespace NotificationFunction
{
    public class CosmosDetectionCounter : IDetectionCounter
    {
        private readonly CosmosClient _cosmosClient;

        public CosmosDetectionCounter(CosmosClient cosmosClient)
        {
            _cosmosClient = cosmosClient;
        }

        public async Task<int> CountRecentAsync(string locationId, int periodMinutes)
        {
            string databaseName = Environment.GetEnvironmentVariable("CosmosDbDatabase")
                ?? throw new InvalidOperationException("CosmosDbDatabase environment variable is not configured");
            string containerName = Environment.GetEnvironmentVariable("CosmosDbContainer")
                ?? throw new InvalidOperationException("CosmosDbContainer environment variable is not configured");

            Container container = _cosmosClient.GetContainer(databaseName, containerName);
            long cutoffTimestamp = DateTimeOffset.UtcNow.AddMinutes(-periodMinutes).ToUnixTimeSeconds();

            // Use TOP 2 rather than COUNT so the query can short-circuit once the threshold is found.
            var query = new QueryDefinition(
                "SELECT TOP 2 c.id FROM c WHERE c.location.id = @locationId AND c.timestamp >= @cutoffTime")
                .WithParameter("@locationId", locationId)
                .WithParameter("@cutoffTime", cutoffTimestamp);

            int count = 0;
            using FeedIterator<JsonElement> iterator = container.GetItemQueryIterator<JsonElement>(query);
            while (iterator.HasMoreResults && count < 2)
            {
                FeedResponse<JsonElement> results = await iterator.ReadNextAsync();
                count += results.Count;
            }
            return count;
        }
    }
}
