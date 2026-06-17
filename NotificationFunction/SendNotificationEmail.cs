using Amazon.SimpleEmail;
using Amazon.SimpleEmail.Model;
using Azure;
using Azure.Data.Tables;
using Microsoft.Azure.Cosmos;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading.Tasks;

namespace NotificationFunction
{
    public class SendNotificationEmail
    {
        private readonly ILogger _logger;
        private readonly IAmazonSimpleEmailService _sesClient;
        private readonly CosmosClient _cosmosClient;

        public SendNotificationEmail(
            ILogger<SendNotificationEmail> logger,
            IAmazonSimpleEmailService sesClient,
            CosmosClient cosmosClient)
        {
            _logger = logger;
            _sesClient = sesClient;
            _cosmosClient = cosmosClient;
        }

        [Function("SendNotificationEmail")]
        public async Task Run(
            [CosmosDBTrigger(
                databaseName: "%CosmosDbDatabase%",
                containerName: "%CosmosDbContainer%",
                Connection = "CosmosDbConnection",
                LeaseContainerName = "leases",
                CreateLeaseContainerIfNotExists = true)] IReadOnlyList<JsonElement> input,
            [TableInput("NotificationState", Connection = "StorageConnection")] TableClient tableClient)
        {
            if (input == null || input.Count == 0)
            {
                _logger.LogInformation("No updated records");
                return;
            }

            string? nodeName = Environment.GetEnvironmentVariable("NODE_NAME");
            string? emailDestination = Environment.GetEnvironmentVariable("EMAIL_DESTINATION");
            string? senderEmail = Environment.GetEnvironmentVariable("SenderEmail");

            if (string.IsNullOrEmpty(nodeName))
            {
                _logger.LogError("NODE_NAME environment variable is not configured");
                return;
            }

            if (string.IsNullOrEmpty(emailDestination))
            {
                _logger.LogError("EMAIL_DESTINATION environment variable is not configured");
                return;
            }

            if (string.IsNullOrEmpty(senderEmail))
            {
                _logger.LogError("SenderEmail environment variable is not configured");
                return;
            }

            int notificationPeriodMinutes = int.TryParse(
                Environment.GetEnvironmentVariable("NOTIFICATION_PERIOD_MINUTES"), out int np) ? np : 60;
            int detectionPeriodMinutes = int.TryParse(
                Environment.GetEnvironmentVariable("DETECTION_PERIOD_MINUTES"), out int dp) ? dp : 15;

            // Check if any triggered document matches NODE_NAME and is an unreviewed detection
            bool hasMatchingDetection = false;
            foreach (JsonElement doc in input)
            {
                if (doc.TryGetProperty("reviewed", out JsonElement reviewed) &&
                    reviewed.ValueKind == JsonValueKind.True)
                {
                    continue;
                }
                if (doc.TryGetProperty("location", out JsonElement location) &&
                    location.TryGetProperty("name", out JsonElement locationName) &&
                    locationName.GetString() == nodeName)
                {
                    hasMatchingDetection = true;
                    break;
                }
            }

            if (!hasMatchingDetection)
            {
                _logger.LogInformation("No unreviewed detections for node {NodeName}", nodeName);
                return;
            }

            // Check if a notification was sent within NOTIFICATION_PERIOD_MINUTES
            DateTime? lastNotificationTime = await GetLastNotificationTimeAsync(tableClient, nodeName);
            if (lastNotificationTime.HasValue &&
                DateTime.UtcNow - lastNotificationTime.Value < TimeSpan.FromMinutes(notificationPeriodMinutes))
            {
                _logger.LogInformation(
                    "Notification rate limited for {NodeName}. Last sent {Minutes:F1} minutes ago.",
                    nodeName,
                    (DateTime.UtcNow - lastNotificationTime.Value).TotalMinutes);
                return;
            }

            // Check if at least one other detection from NODE_NAME occurred in past DETECTION_PERIOD_MINUTES
            int recentDetectionCount = await CountRecentDetectionsAsync(nodeName, detectionPeriodMinutes);
            if (recentDetectionCount < 2)
            {
                _logger.LogInformation(
                    "Insufficient recent detections for {NodeName}: {Count} in past {Minutes} minutes (need at least 2)",
                    nodeName, recentDetectionCount, detectionPeriodMinutes);
                return;
            }

            // Send email notification
            string subject = $"Whale detection at {nodeName}";
            string body = $"<p>A whale has been detected at <strong>{nodeName}</strong>.</p>" +
                          $"<p>There have been {recentDetectionCount} detections in the past {detectionPeriodMinutes} minutes.</p>" +
                          $"<p>Time: {DateTime.UtcNow:u}</p>";

            var emailRequest = new SendEmailRequest
            {
                Source = senderEmail,
                Destination = new Destination(new List<string> { emailDestination }),
                Message = new Message
                {
                    Subject = new Content(subject),
                    Body = new Body
                    {
                        Html = new Content { Charset = "UTF-8", Data = body }
                    }
                }
            };

            await _sesClient.SendEmailAsync(emailRequest);
            _logger.LogInformation(
                "Sent notification email for {NodeName}",
                nodeName);

            // Update last notification time
            await UpdateLastNotificationTimeAsync(tableClient, nodeName);
        }

        private async Task<DateTime?> GetLastNotificationTimeAsync(TableClient tableClient, string nodeName)
        {
            try
            {
                Azure.Response<NotificationStateEntity> response =
                    await tableClient.GetEntityAsync<NotificationStateEntity>("NotificationState", nodeName);
                return response.Value.LastNotificationTime;
            }
            catch (RequestFailedException ex) when (ex.Status == 404)
            {
                return null;
            }
        }

        private async Task UpdateLastNotificationTimeAsync(TableClient tableClient, string nodeName)
        {
            var entity = new NotificationStateEntity
            {
                PartitionKey = "NotificationState",
                RowKey = nodeName,
                LastNotificationTime = DateTime.UtcNow
            };
            await tableClient.UpsertEntityAsync(entity);
        }

        private async Task<int> CountRecentDetectionsAsync(string nodeName, int periodMinutes)
        {
            string databaseName = Environment.GetEnvironmentVariable("CosmosDbDatabase") ?? "detections";
            string containerName = Environment.GetEnvironmentVariable("CosmosDbContainer") ?? "metadata";

            Container container = _cosmosClient.GetContainer(databaseName, containerName);
            long cutoffTimestamp = DateTimeOffset.UtcNow.AddMinutes(-periodMinutes).ToUnixTimeSeconds();

            // Use TOP 2 rather than COUNT so the query can short-circuit once the threshold is found
            var query = new QueryDefinition(
                "SELECT TOP 2 c.id FROM c WHERE c.location.name = @nodeName AND c._ts >= @cutoffTime")
                .WithParameter("@nodeName", nodeName)
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
