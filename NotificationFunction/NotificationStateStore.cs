// SPDX-License-Identifier: MIT
using Microsoft.Azure.Cosmos;
using System;
using System.Net;
using System.Text.Json;
using System.Threading.Tasks;

namespace NotificationFunction
{
    public class NotificationStateStore : INotificationStateStore, IDisposable
    {
        private readonly CosmosClient _cosmosClient;
        private readonly string _databaseName;
        private readonly string _containerName;
        private readonly object _containerLock = new();
        private Task<Container>? _containerTask;

        public NotificationStateStore(string connectionString, string databaseName, string containerName)
        {
            var options = new CosmosClientOptions
            {
                Serializer = new CosmosSystemTextJsonSerializer(new JsonSerializerOptions
                {
                    PropertyNamingPolicy = JsonNamingPolicy.CamelCase
                })
            };
            _cosmosClient = new CosmosClient(connectionString, options);
            _databaseName = databaseName;
            _containerName = containerName;
        }

        public async Task<DateTime?> GetLastNotificationTimeAsync(string locationId)
        {
            Container container = await GetContainerAsync();

            try
            {
                ItemResponse<NotificationStateEntity> response =
                    await container.ReadItemAsync<NotificationStateEntity>(locationId, new PartitionKey(locationId));

                if (DateTime.TryParse(response.Resource.LastNotificationTime, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind, out DateTime result))
                {
                    return result;
                }
                return null;
            }
            catch (CosmosException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
            {
                return null;
            }
        }

        public async Task UpdateLastNotificationTimeAsync(string locationId)
        {
            Container container = await GetContainerAsync();
            var entity = new NotificationStateEntity
            {
                Id = locationId,
                LastNotificationTime = DateTime.UtcNow.ToString("o", System.Globalization.CultureInfo.InvariantCulture)
            };

            await container.UpsertItemAsync(entity, new PartitionKey(locationId));
        }

        public void Dispose()
        {
            _cosmosClient.Dispose();
        }

        private Task<Container> GetContainerAsync()
        {
            lock (_containerLock)
            {
                _containerTask ??= CreateContainerAsync();
                return _containerTask;
            }
        }

        private async Task<Container> CreateContainerAsync()
        {
            DatabaseResponse database = await _cosmosClient.CreateDatabaseIfNotExistsAsync(_databaseName);
            ContainerResponse container = await database.Database.CreateContainerIfNotExistsAsync(
                new ContainerProperties(_containerName, "/id"));
            return container.Container;
        }
    }

    internal class CosmosSystemTextJsonSerializer : CosmosSerializer
    {
        private readonly JsonSerializerOptions _options;

        public CosmosSystemTextJsonSerializer(JsonSerializerOptions options)
        {
            _options = options;
        }

        public override T FromStream<T>(System.IO.Stream stream)
        {
            using (stream)
            {
                if (stream.CanSeek && stream.Length == 0)
                {
                    return default!;
                }

                return JsonSerializer.Deserialize<T>(stream, _options)!;
            }
        }

        public override System.IO.Stream ToStream<T>(T input)
        {
            var stream = new System.IO.MemoryStream();
            JsonSerializer.Serialize(stream, input, _options);
            stream.Position = 0;
            return stream;
        }
    }
}
