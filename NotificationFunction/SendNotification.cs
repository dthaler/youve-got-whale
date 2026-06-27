// SPDX-License-Identifier: MIT
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

namespace NotificationFunction
{
    public class SendNotification
    {
        private readonly ILogger _logger;
        private readonly IHttpClientFactory _httpClientFactory;
        private readonly IDetectionCounter _detectionCounter;
        private readonly INotificationStateStore _notificationStateStore;

        public SendNotification(
            ILogger<SendNotification> logger,
            IHttpClientFactory httpClientFactory,
            IDetectionCounter detectionCounter,
            INotificationStateStore notificationStateStore)
        {
            _logger = logger;
            _httpClientFactory = httpClientFactory;
            _detectionCounter = detectionCounter;
            _notificationStateStore = notificationStateStore;
        }

        [Function("SendNotification")]
        public async Task Run(
            [CosmosDBTrigger(
                databaseName: "%CosmosDbDatabase%",
                containerName: "%CosmosDbContainer%",
                Connection = "CosmosDbConnection",
                LeaseContainerName = "leases",
                CreateLeaseContainerIfNotExists = true)] IReadOnlyList<JsonElement> input)
        {
            if (input == null || input.Count == 0)
            {
                _logger.LogInformation("No updated records");
                return;
            }

            string? locationId = Environment.GetEnvironmentVariable("LocationId");
            string? appNotificationUrl = Environment.GetEnvironmentVariable("AppNotificationUrl");

            if (string.IsNullOrEmpty(locationId))
            {
                _logger.LogError("LocationId environment variable is not configured");
                return;
            }

            if (string.IsNullOrEmpty(appNotificationUrl))
            {
                _logger.LogError("AppNotificationUrl environment variable is not configured");
                return;
            }

            int notificationPeriodMinutes = int.TryParse(
                Environment.GetEnvironmentVariable("NotificationPeriodMinutes"), out int np) ? np : 60;
            int detectionPeriodMinutes = int.TryParse(
                Environment.GetEnvironmentVariable("DetectionPeriodMinutes"), out int dp) ? dp : 15;

            await ProcessDocumentsAsync(
                input, locationId, appNotificationUrl,
                notificationPeriodMinutes, detectionPeriodMinutes);
        }

        /// <summary>
        /// Core notification logic, factored out of Run for testability.
        /// Inspects <paramref name="input"/> for an unreviewed detection matching
        /// <paramref name="locationId"/>, enforces rate-limiting and a minimum
        /// cluster size, then posts a notification when all conditions are satisfied.
        /// </summary>
        public async Task ProcessDocumentsAsync(
            IReadOnlyList<JsonElement> input,
            string locationId,
            string appNotificationUrl,
            int notificationPeriodMinutes,
            int detectionPeriodMinutes)
        {
            // Check if any triggered document matches LocationId.
            string nodeName = string.Empty;
            string comments = string.Empty;
            bool hasMatchingDetection = false;
            foreach (JsonElement doc in input)
            {
                if (doc.TryGetProperty("location", out JsonElement location) &&
                    location.TryGetProperty("id", out JsonElement locationIdElement) &&
                    locationIdElement.GetString() == locationId)
                {
                    hasMatchingDetection = true;
                    if (location.TryGetProperty("name", out JsonElement locationName))
                    {
                        nodeName = locationName.GetString() ?? locationId;
                    }
                    if (doc.TryGetProperty("comments", out JsonElement commentsElement))
                    {
                        comments = commentsElement.ValueKind == JsonValueKind.String
                            ? commentsElement.GetString() ?? string.Empty
                            : string.Empty;
                    }
                    break;
                }
            }

            if (!hasMatchingDetection)
            {
                _logger.LogInformation("No unreviewed detections for node {LocationId}", locationId);
                return;
            }

            // Check if a notification was sent within NotificationPeriodMinutes.
            DateTime? lastNotificationTime = await _notificationStateStore.GetLastNotificationTimeAsync(locationId);
            if (lastNotificationTime.HasValue &&
                DateTime.UtcNow - lastNotificationTime.Value < TimeSpan.FromMinutes(notificationPeriodMinutes))
            {
                _logger.LogInformation(
                    "Notification rate limited for {NodeName}. Last sent {Minutes:F1} minutes ago.",
                    nodeName,
                    (DateTime.UtcNow - lastNotificationTime.Value).TotalMinutes);
                return;
            }

            // Check if at least one other detection from LocationId occurred in past DetectionPeriodMinutes.
            int recentDetectionCount = await _detectionCounter.CountRecentAsync(locationId, detectionPeriodMinutes);
            if (recentDetectionCount < 2)
            {
                _logger.LogInformation(
                    "Insufficient recent detections for {NodeName}: {Count} in past {Minutes} minutes (need at least 2)",
                    nodeName, recentDetectionCount, detectionPeriodMinutes);
                return;
            }

            string category = "Whale";
            if (comments.StartsWith("AI: "))
            {
                string remainder = comments.Substring(4);
                int spaceIndex = remainder.IndexOf(' ');
                category = spaceIndex >= 0 ? remainder.Substring(0, spaceIndex) : remainder;
            }

            // Send notification via HTTP POST using IFTTT's webhook JSON payload format:
            //   value1 = category (e.g. "Whale" or AI-identified species)
            //   value2 = node name (hydrophone location)
            //   value3 = reserved / empty
            var payload = new { value1 = category, value2 = nodeName, value3 = string.Empty };
            string json = JsonSerializer.Serialize(payload);
            var content = new StringContent(json, Encoding.UTF8, "application/json");
            var httpClient = _httpClientFactory.CreateClient();
            HttpResponseMessage response = await httpClient.PostAsync(appNotificationUrl, content);
            if (response.IsSuccessStatusCode)
            {
                _logger.LogInformation(
                    "Sent notification for {NodeName}: {StatusCode}",
                    nodeName, response.StatusCode);
            }
            else
            {
                _logger.LogWarning(
                    "Notification POST for {NodeName} returned non-success status: {StatusCode}",
                    nodeName, response.StatusCode);
            }

            // Update last notification time.
            await _notificationStateStore.UpdateLastNotificationTimeAsync(locationId);
        }
    }
}
