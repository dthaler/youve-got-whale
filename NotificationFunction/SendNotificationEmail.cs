// SPDX-License-Identifier: MIT
using Amazon.SimpleEmail;
using Amazon.SimpleEmail.Model;
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
        private readonly IDetectionCounter _detectionCounter;
        private readonly INotificationStateStore _notificationStateStore;

        public SendNotificationEmail(
            ILogger<SendNotificationEmail> logger,
            IAmazonSimpleEmailService sesClient,
            IDetectionCounter detectionCounter,
            INotificationStateStore notificationStateStore)
        {
            _logger = logger;
            _sesClient = sesClient;
            _detectionCounter = detectionCounter;
            _notificationStateStore = notificationStateStore;
        }

        [Function("SendNotificationEmail")]
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
            string? recipientEmail = Environment.GetEnvironmentVariable("RecipientEmail");
            string? senderEmail = Environment.GetEnvironmentVariable("SenderEmail");

            if (string.IsNullOrEmpty(locationId))
            {
                _logger.LogError("LocationId environment variable is not configured");
                return;
            }

            if (string.IsNullOrEmpty(recipientEmail))
            {
                _logger.LogError("RecipientEmail environment variable is not configured");
                return;
            }

            if (string.IsNullOrEmpty(senderEmail))
            {
                _logger.LogError("SenderEmail environment variable is not configured");
                return;
            }

            int notificationPeriodMinutes = int.TryParse(
                Environment.GetEnvironmentVariable("NotificationPeriodMinutes"), out int np) ? np : 60;
            int detectionPeriodMinutes = int.TryParse(
                Environment.GetEnvironmentVariable("DetectionPeriodMinutes"), out int dp) ? dp : 15;

            await ProcessDocumentsAsync(
                input, locationId, recipientEmail, senderEmail,
                notificationPeriodMinutes, detectionPeriodMinutes);
        }

        /// <summary>
        /// Core notification logic, factored out of Run for testability.
        /// Inspects <paramref name="input"/> for an unreviewed detection matching
        /// <paramref name="locationId"/>, enforces rate-limiting and a minimum
        /// cluster size, then sends an email when all conditions are satisfied.
        /// </summary>
        public async Task ProcessDocumentsAsync(
            IReadOnlyList<JsonElement> input,
            string locationId,
            string recipientEmail,
            string senderEmail,
            int notificationPeriodMinutes,
            int detectionPeriodMinutes)
        {
            // Check if any triggered document matches LocationId and is an unreviewed detection.
            string nodeName = string.Empty;
            bool hasMatchingDetection = false;
            foreach (JsonElement doc in input)
            {
                if (doc.TryGetProperty("reviewed", out JsonElement reviewed) &&
                    reviewed.ValueKind == JsonValueKind.True)
                {
                    continue;
                }
                if (doc.TryGetProperty("location", out JsonElement location) &&
                    location.TryGetProperty("id", out JsonElement locationIdElement) &&
                    locationIdElement.GetString() == locationId)
                {
                    hasMatchingDetection = true;
                    if (location.TryGetProperty("name", out JsonElement locationName))
                    {
                        nodeName = locationName.GetString() ?? locationId;
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

            // Send email notification.
            string subject = $"Whale detection at {nodeName}";
            string body = $"<p>A whale has been detected at <strong>{nodeName}</strong>.</p>" +
                          $"<p>There have been {recentDetectionCount} detections in the past {detectionPeriodMinutes} minutes.</p>" +
                          $"<p>Time: {DateTime.UtcNow:u}</p>";

            var emailRequest = new SendEmailRequest
            {
                Source = senderEmail,
                Destination = new Destination(new List<string> { recipientEmail }),
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

            // Update last notification time.
            await _notificationStateStore.UpdateLastNotificationTimeAsync(locationId);
        }
    }
}
