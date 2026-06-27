// SPDX-License-Identifier: MIT
using Moq;
using Moq.Protected;
using NotificationFunction;
using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace NotificationFunction.Tests.Integration
{
    /// <summary>
    /// Integration tests for <see cref="SendNotification.ProcessDocumentsAsync"/>.
    /// These tests exercise the full notification pipeline with real in-process
    /// logic, but replace the three external I/O boundaries
    /// (<see cref="HttpClient"/>, <see cref="IDetectionCounter"/>,
    /// and <see cref="INotificationStateStore"/>) with mocks so the suite
    /// runs without any cloud infrastructure.
    /// </summary>
    public class SendNotificationIntegrationTests
    {
        private const string LocationId = "rpi_orcasound_lab";
        private const string NodeName = "Orcasound Lab";
        private const string AppNotificationUrl = "https://maker.ifttt.com/trigger/whale/json/with/key/testkey";
        private const string Comments = "AI: resident and vessel";
        private const int NotificationPeriodMinutes = 60;
        private const int DetectionPeriodMinutes = 15;

        // ---------------------------------------------------------------------------
        // Helpers
        // ---------------------------------------------------------------------------

        private static JsonElement MakeDetection(string locationId, string locationName, string? comments, bool? reviewed = null)
        {
            object doc = reviewed.HasValue
                ? new {
                    reviewed = reviewed.Value,
                    comments = comments,
                    location = new {
                        id = locationId,
                        name = locationName }
                }
                : (object)new {
                    comments = comments,
                    location = new {
                        id = locationId,
                        name = locationName }
                };
            return JsonSerializer.SerializeToElement(doc);
        }

        private static (SendNotification function,
                         Mock<HttpMessageHandler> handlerMock,
                         Mock<IDetectionCounter> detectionCounterMock,
                         Mock<INotificationStateStore> stateMock)
            BuildFunction(
                int recentDetections = 2,
                DateTime? lastNotificationTime = null)
        {
            var handlerMock = new Mock<HttpMessageHandler>();
            handlerMock.Protected()
                .Setup<Task<HttpResponseMessage>>("SendAsync",
                    ItExpr.IsAny<HttpRequestMessage>(),
                    ItExpr.IsAny<CancellationToken>())
                .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK));

            var detectionCounterMock = new Mock<IDetectionCounter>();
            detectionCounterMock.Setup(x => x.CountRecentAsync(It.IsAny<string>(), It.IsAny<int>()))
                                 .ReturnsAsync(recentDetections);

            var stateMock = new Mock<INotificationStateStore>();
            stateMock.Setup(x => x.GetLastNotificationTimeAsync(It.IsAny<string>()))
                     .ReturnsAsync(lastNotificationTime);

            var loggerMock = new Mock<Microsoft.Extensions.Logging.ILogger<SendNotification>>();
            var function = new SendNotification(
                loggerMock.Object,
                new HttpClient(handlerMock.Object),
                detectionCounterMock.Object,
                stateMock.Object);

            return (function, handlerMock, detectionCounterMock, stateMock);
        }

        // ---------------------------------------------------------------------------
        // Tests: happy path
        // ---------------------------------------------------------------------------

        /// <summary>
        /// End-to-end: a detection for the configured location triggers
        /// a notification POST to AppNotificationUrl with the correct payload.
        /// </summary>
        [Fact]
        public async Task ProcessDocumentsAsync_SendsNotification_ForDetection()
        {
            var (function, handlerMock, _, stateMock) = BuildFunction();

            var input = new List<JsonElement>
            {
                MakeDetection(LocationId, NodeName, Comments)
            };

            await function.ProcessDocumentsAsync(input, LocationId, AppNotificationUrl,
                NotificationPeriodMinutes, DetectionPeriodMinutes);

            handlerMock.Protected().Verify(
                "SendAsync",
                Times.Once(),
                ItExpr.Is<HttpRequestMessage>(r =>
                    r.Method == HttpMethod.Post &&
                    r.RequestUri != null &&
                    r.RequestUri.ToString() == AppNotificationUrl),
                ItExpr.IsAny<CancellationToken>());

            stateMock.Verify(x => x.UpdateLastNotificationTimeAsync(LocationId), Times.Once);
        }

        /// <summary>
        /// When the reviewed field is absent the detection is treated as unreviewed.
        /// </summary>
        [Fact]
        public async Task ProcessDocumentsAsync_SendsNotification_WhenReviewedFieldMissing()
        {
            var (function, handlerMock, _, _) = BuildFunction();

            // No "reviewed" property in the document.
            var input = new List<JsonElement> { MakeDetection(LocationId, NodeName, Comments) };

            await function.ProcessDocumentsAsync(input, LocationId, AppNotificationUrl,
                NotificationPeriodMinutes, DetectionPeriodMinutes);

            handlerMock.Protected().Verify(
                "SendAsync",
                Times.Once(),
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>());
        }

        // ---------------------------------------------------------------------------
        // Tests: suppression – rate limiting
        // ---------------------------------------------------------------------------

        /// <summary>
        /// A notification was sent recently; rate limiting prevents another one.
        /// </summary>
        [Fact]
        public async Task ProcessDocumentsAsync_DoesNotSendNotification_WhenRateLimited()
        {
            // Last notification 10 minutes ago; period is 60 minutes.
            var (function, handlerMock, _, _) = BuildFunction(lastNotificationTime: DateTime.UtcNow.AddMinutes(-10));

            var input = new List<JsonElement> { MakeDetection(LocationId, NodeName, Comments, reviewed: false) };

            await function.ProcessDocumentsAsync(input, LocationId, AppNotificationUrl,
                NotificationPeriodMinutes, DetectionPeriodMinutes);

            handlerMock.Protected().Verify(
                "SendAsync",
                Times.Never(),
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>());
        }

        /// <summary>
        /// The rate-limit window has passed; a notification should be sent.
        /// </summary>
        [Fact]
        public async Task ProcessDocumentsAsync_SendsNotification_WhenRateLimitWindowHasExpired()
        {
            // Last notification 90 minutes ago; period is 60 minutes.
            var (function, handlerMock, _, _) = BuildFunction(lastNotificationTime: DateTime.UtcNow.AddMinutes(-90));

            var input = new List<JsonElement> { MakeDetection(LocationId, NodeName, Comments, reviewed: false) };

            await function.ProcessDocumentsAsync(input, LocationId, AppNotificationUrl,
                NotificationPeriodMinutes, DetectionPeriodMinutes);

            handlerMock.Protected().Verify(
                "SendAsync",
                Times.Once(),
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>());
        }

        // ---------------------------------------------------------------------------
        // Tests: suppression – insufficient detections
        // ---------------------------------------------------------------------------

        /// <summary>
        /// Only one recent detection in the window; the two-detection threshold is not met.
        /// </summary>
        [Fact]
        public async Task ProcessDocumentsAsync_DoesNotSendNotification_WhenInsufficientRecentDetections()
        {
            var (function, handlerMock, _, _) = BuildFunction(recentDetections: 1);

            var input = new List<JsonElement> { MakeDetection(LocationId, NodeName, Comments, reviewed: false) };

            await function.ProcessDocumentsAsync(input, LocationId, AppNotificationUrl,
                NotificationPeriodMinutes, DetectionPeriodMinutes);

            handlerMock.Protected().Verify(
                "SendAsync",
                Times.Never(),
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>());
        }

        // ---------------------------------------------------------------------------
        // Tests: notification payload content
        // ---------------------------------------------------------------------------

        /// <summary>
        /// The JSON payload contains the node name and category.
        /// </summary>
        [Fact]
        public async Task ProcessDocumentsAsync_Payload_ContainsNodeNameAndCategory()
        {
            HttpRequestMessage? capturedRequest = null;
            var handlerMock = new Mock<HttpMessageHandler>();
            handlerMock.Protected()
                .Setup<Task<HttpResponseMessage>>("SendAsync",
                    ItExpr.IsAny<HttpRequestMessage>(),
                    ItExpr.IsAny<CancellationToken>())
                .Callback<HttpRequestMessage, CancellationToken>((req, _) => capturedRequest = req)
                .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK));

            var detectionCounterMock = new Mock<IDetectionCounter>();
            detectionCounterMock.Setup(x => x.CountRecentAsync(LocationId, DetectionPeriodMinutes))
                                 .ReturnsAsync(4);

            var stateMock = new Mock<INotificationStateStore>();
            stateMock.Setup(x => x.GetLastNotificationTimeAsync(LocationId))
                     .ReturnsAsync((DateTime?)null);

            var loggerMock = new Mock<Microsoft.Extensions.Logging.ILogger<SendNotification>>();
            var function = new SendNotification(
                loggerMock.Object,
                new HttpClient(handlerMock.Object),
                detectionCounterMock.Object,
                stateMock.Object);

            var input = new List<JsonElement> { MakeDetection(LocationId, NodeName, Comments, reviewed: false) };
            await function.ProcessDocumentsAsync(input, LocationId, AppNotificationUrl,
                NotificationPeriodMinutes, DetectionPeriodMinutes);

            Assert.NotNull(capturedRequest);
            string body = await capturedRequest!.Content!.ReadAsStringAsync();
            Assert.Contains(NodeName, body);
            Assert.Contains("resident", body);
            Assert.Contains("application/json", capturedRequest.Content.Headers.ContentType?.ToString());
        }
    }
}
