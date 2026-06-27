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

namespace NotificationFunction.Tests.Unit
{
    /// <summary>
    /// Unit tests for the document-matching and notification-suppression logic in
    /// <see cref="SendNotification.ProcessDocumentsAsync"/>.
    /// All external dependencies (HttpClient, detection counter, state store) are mocked so
    /// these tests run without any network or cloud connections.
    /// </summary>
    public class SendNotificationTests
    {
        private const string LocationId = "rpi_orcasound_lab";
        private const string NodeName = "Orcasound Lab";
        private const string AppNotificationUrl = "https://maker.ifttt.com/trigger/whale/json/with/key/testkey";
        private const int NotificationPeriodMinutes = 60;
        private const int DetectionPeriodMinutes = 15;

        // ---------------------------------------------------------------------------
        // Helpers
        // ---------------------------------------------------------------------------

        private static JsonElement MakeDetection(string locationId, string locationName, bool? reviewed = null)
        {
            object doc = reviewed.HasValue
                ? new { reviewed = reviewed.Value, location = new { id = locationId, name = locationName } }
                : (object)new { location = new { id = locationId, name = locationName } };
            return JsonSerializer.SerializeToElement(doc);
        }

        private static (SendNotification function, Mock<HttpMessageHandler> handlerMock)
            BuildFunction(
                Mock<HttpMessageHandler>? handlerMock = null,
                Mock<IDetectionCounter>? detectionCounterMock = null,
                Mock<INotificationStateStore>? stateMock = null)
        {
            bool newHandler = handlerMock == null;
            handlerMock ??= new Mock<HttpMessageHandler>();

            if (newHandler)
            {
                handlerMock.Protected()
                    .Setup<Task<HttpResponseMessage>>("SendAsync",
                        ItExpr.IsAny<HttpRequestMessage>(),
                        ItExpr.IsAny<CancellationToken>())
                    .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK));
            }

            detectionCounterMock ??= new Mock<IDetectionCounter>();
            stateMock ??= new Mock<INotificationStateStore>();

            var httpClient = new HttpClient(handlerMock.Object);
            var factoryMock = new Mock<IHttpClientFactory>();
            factoryMock.Setup(f => f.CreateClient(It.IsAny<string>())).Returns(httpClient);

            var loggerMock = new Mock<Microsoft.Extensions.Logging.ILogger<SendNotification>>();
            var function = new SendNotification(
                loggerMock.Object,
                factoryMock.Object,
                detectionCounterMock.Object,
                stateMock.Object);

            return (function, handlerMock);
        }

        // ---------------------------------------------------------------------------
        // Tests: document matching
        // ---------------------------------------------------------------------------

        [Fact]
        public async Task ProcessDocumentsAsync_SendsNotification_ForDetection()
        {
            var handlerMock = new Mock<HttpMessageHandler>();
            handlerMock.Protected()
                .Setup<Task<HttpResponseMessage>>("SendAsync",
                    ItExpr.IsAny<HttpRequestMessage>(),
                    ItExpr.IsAny<CancellationToken>())
                .ReturnsAsync(new HttpResponseMessage(HttpStatusCode.OK));

            var detectionCounterMock = new Mock<IDetectionCounter>();
            detectionCounterMock.Setup(x => x.CountRecentAsync(LocationId, DetectionPeriodMinutes))
                                 .ReturnsAsync(3);

            var stateMock = new Mock<INotificationStateStore>();
            stateMock.Setup(x => x.GetLastNotificationTimeAsync(LocationId))
                     .ReturnsAsync((DateTime?)null);

            var (function, _) = BuildFunction(handlerMock, detectionCounterMock, stateMock);

            var input = new List<JsonElement> { MakeDetection(LocationId, NodeName, reviewed: false) };
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

        [Fact]
        public async Task ProcessDocumentsAsync_SendsNotification_WhenReviewedFieldMissing()
        {
            var detectionCounterMock = new Mock<IDetectionCounter>();
            detectionCounterMock.Setup(x => x.CountRecentAsync(LocationId, DetectionPeriodMinutes))
                                 .ReturnsAsync(2);

            var stateMock = new Mock<INotificationStateStore>();
            stateMock.Setup(x => x.GetLastNotificationTimeAsync(LocationId))
                     .ReturnsAsync((DateTime?)null);

            var (function, handlerMock) = BuildFunction(detectionCounterMock: detectionCounterMock, stateMock: stateMock);

            // Document has no "reviewed" field – should be treated as unreviewed.
            var input = new List<JsonElement> { MakeDetection(LocationId, NodeName) };
            await function.ProcessDocumentsAsync(input, LocationId, AppNotificationUrl,
                NotificationPeriodMinutes, DetectionPeriodMinutes);

            handlerMock.Protected().Verify(
                "SendAsync",
                Times.Once(),
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>());
        }

        [Fact]
        public async Task ProcessDocumentsAsync_DoesNotSendNotification_WhenNoMatchingLocation()
        {
            var (function, handlerMock) = BuildFunction();

            // Document belongs to a different location.
            var input = new List<JsonElement> { MakeDetection("rpi_other_location", "Other Location") };
            await function.ProcessDocumentsAsync(input, LocationId, AppNotificationUrl,
                NotificationPeriodMinutes, DetectionPeriodMinutes);

            handlerMock.Protected().Verify(
                "SendAsync",
                Times.Never(),
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>());
        }

        // ---------------------------------------------------------------------------
        // Tests: rate limiting
        // ---------------------------------------------------------------------------

        [Fact]
        public async Task ProcessDocumentsAsync_DoesNotSendNotification_WhenRateLimited()
        {
            var stateMock = new Mock<INotificationStateStore>();
            // Last notification was 10 minutes ago; limit is 60 minutes.
            stateMock.Setup(x => x.GetLastNotificationTimeAsync(LocationId))
                     .ReturnsAsync(DateTime.UtcNow.AddMinutes(-10));

            var (function, handlerMock) = BuildFunction(stateMock: stateMock);

            var input = new List<JsonElement> { MakeDetection(LocationId, NodeName, reviewed: false) };
            await function.ProcessDocumentsAsync(input, LocationId, AppNotificationUrl,
                NotificationPeriodMinutes, DetectionPeriodMinutes);

            handlerMock.Protected().Verify(
                "SendAsync",
                Times.Never(),
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>());
        }

        [Fact]
        public async Task ProcessDocumentsAsync_SendsNotification_WhenRateLimitHasExpired()
        {
            var detectionCounterMock = new Mock<IDetectionCounter>();
            detectionCounterMock.Setup(x => x.CountRecentAsync(LocationId, DetectionPeriodMinutes))
                                 .ReturnsAsync(2);

            var stateMock = new Mock<INotificationStateStore>();
            // Last notification was 90 minutes ago; limit is 60 minutes – should proceed.
            stateMock.Setup(x => x.GetLastNotificationTimeAsync(LocationId))
                     .ReturnsAsync(DateTime.UtcNow.AddMinutes(-90));

            var (function, handlerMock) = BuildFunction(detectionCounterMock: detectionCounterMock, stateMock: stateMock);

            var input = new List<JsonElement> { MakeDetection(LocationId, NodeName, reviewed: false) };
            await function.ProcessDocumentsAsync(input, LocationId, AppNotificationUrl,
                NotificationPeriodMinutes, DetectionPeriodMinutes);

            handlerMock.Protected().Verify(
                "SendAsync",
                Times.Once(),
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>());
        }

        // ---------------------------------------------------------------------------
        // Tests: detection cluster threshold
        // ---------------------------------------------------------------------------

        [Fact]
        public async Task ProcessDocumentsAsync_DoesNotSendNotification_WhenInsufficientRecentDetections()
        {
            var detectionCounterMock = new Mock<IDetectionCounter>();
            // Only 1 recent detection – threshold requires at least 2.
            detectionCounterMock.Setup(x => x.CountRecentAsync(LocationId, DetectionPeriodMinutes))
                                 .ReturnsAsync(1);

            var stateMock = new Mock<INotificationStateStore>();
            stateMock.Setup(x => x.GetLastNotificationTimeAsync(LocationId))
                     .ReturnsAsync((DateTime?)null);

            var (function, handlerMock) = BuildFunction(detectionCounterMock: detectionCounterMock, stateMock: stateMock);

            var input = new List<JsonElement> { MakeDetection(LocationId, NodeName, reviewed: false) };
            await function.ProcessDocumentsAsync(input, LocationId, AppNotificationUrl,
                NotificationPeriodMinutes, DetectionPeriodMinutes);

            handlerMock.Protected().Verify(
                "SendAsync",
                Times.Never(),
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>());
        }

        [Fact]
        public async Task ProcessDocumentsAsync_SendsNotification_WhenDetectionCountMeetsThreshold()
        {
            var detectionCounterMock = new Mock<IDetectionCounter>();
            detectionCounterMock.Setup(x => x.CountRecentAsync(LocationId, DetectionPeriodMinutes))
                                 .ReturnsAsync(2);

            var stateMock = new Mock<INotificationStateStore>();
            stateMock.Setup(x => x.GetLastNotificationTimeAsync(LocationId))
                     .ReturnsAsync((DateTime?)null);

            var (function, handlerMock) = BuildFunction(detectionCounterMock: detectionCounterMock, stateMock: stateMock);

            var input = new List<JsonElement> { MakeDetection(LocationId, NodeName, reviewed: false) };
            await function.ProcessDocumentsAsync(input, LocationId, AppNotificationUrl,
                NotificationPeriodMinutes, DetectionPeriodMinutes);

            handlerMock.Protected().Verify(
                "SendAsync",
                Times.Once(),
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>());
        }

        // ---------------------------------------------------------------------------
        // Tests: notification payload content
        // ---------------------------------------------------------------------------

        [Fact]
        public async Task ProcessDocumentsAsync_PayloadContainsNodeName()
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
                                 .ReturnsAsync(3);

            var stateMock = new Mock<INotificationStateStore>();
            stateMock.Setup(x => x.GetLastNotificationTimeAsync(LocationId))
                     .ReturnsAsync((DateTime?)null);

            var (function, _) = BuildFunction(handlerMock, detectionCounterMock, stateMock);

            var input = new List<JsonElement> { MakeDetection(LocationId, NodeName, reviewed: false) };
            await function.ProcessDocumentsAsync(input, LocationId, AppNotificationUrl,
                NotificationPeriodMinutes, DetectionPeriodMinutes);

            Assert.NotNull(capturedRequest);
            string body = await capturedRequest!.Content!.ReadAsStringAsync();
            Assert.Contains(NodeName, body);
        }

        [Fact]
        public async Task ProcessDocumentsAsync_PayloadContainsCategory()
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
                                 .ReturnsAsync(5);

            var stateMock = new Mock<INotificationStateStore>();
            stateMock.Setup(x => x.GetLastNotificationTimeAsync(LocationId))
                     .ReturnsAsync((DateTime?)null);

            var (function, _) = BuildFunction(handlerMock, detectionCounterMock, stateMock);

            var input = new List<JsonElement> { MakeDetection(LocationId, NodeName, reviewed: false) };
            await function.ProcessDocumentsAsync(input, LocationId, AppNotificationUrl,
                NotificationPeriodMinutes, DetectionPeriodMinutes);

            Assert.NotNull(capturedRequest);
            string body = await capturedRequest!.Content!.ReadAsStringAsync();
            // Default category when no comments field present is "Whale"
            Assert.Contains("Whale", body);
        }

        // ---------------------------------------------------------------------------
        // Tests: multiple documents in one batch
        // ---------------------------------------------------------------------------

        [Fact]
        public async Task ProcessDocumentsAsync_SendsNotification_WhenMatchingDocIsNotFirst()
        {
            var detectionCounterMock = new Mock<IDetectionCounter>();
            detectionCounterMock.Setup(x => x.CountRecentAsync(LocationId, DetectionPeriodMinutes))
                                 .ReturnsAsync(2);

            var stateMock = new Mock<INotificationStateStore>();
            stateMock.Setup(x => x.GetLastNotificationTimeAsync(LocationId))
                     .ReturnsAsync((DateTime?)null);

            var (function, handlerMock) = BuildFunction(detectionCounterMock: detectionCounterMock, stateMock: stateMock);

            var input = new List<JsonElement>
            {
                MakeDetection("rpi_other", "Other Node", reviewed: false),
                MakeDetection(LocationId, NodeName, reviewed: false),
            };

            await function.ProcessDocumentsAsync(input, LocationId, AppNotificationUrl,
                NotificationPeriodMinutes, DetectionPeriodMinutes);

            handlerMock.Protected().Verify(
                "SendAsync",
                Times.Once(),
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>());
        }

        [Fact]
        public async Task ProcessDocumentsAsync_DoesNotSendNotification_WhenMatchingDocIsReviewedAndOtherIsNot()
        {
            // The reviewed matching doc should be skipped; unreviewed non-matching doc should not trigger.
            var (function, handlerMock) = BuildFunction();

            var input = new List<JsonElement>
            {
                MakeDetection(LocationId, NodeName, reviewed: true),
                MakeDetection("rpi_other", "Other Node", reviewed: false),
            };

            await function.ProcessDocumentsAsync(input, LocationId, AppNotificationUrl,
                NotificationPeriodMinutes, DetectionPeriodMinutes);

            handlerMock.Protected().Verify(
                "SendAsync",
                Times.Never(),
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>());
        }

        [Fact]
        public async Task ProcessDocumentsAsync_UpdatesLastNotificationTime_WhenNotificationSent()
        {
            // Arrange
            var stateMock = new Mock<INotificationStateStore>();
            var testTime = new DateTime(2026, 1, 25, 7, 12, 0, DateTimeKind.Utc);
            stateMock.Setup(x => x.GetLastNotificationTimeAsync(LocationId))
                     .ReturnsAsync(testTime);
            stateMock.Setup(x => x.UpdateLastNotificationTimeAsync(It.IsAny<string>()))
                     .Returns(Task.CompletedTask);

            var detectionCounterMock = new Mock<IDetectionCounter>();
            detectionCounterMock.Setup(x => x.CountRecentAsync(LocationId, DetectionPeriodMinutes))
                                 .ReturnsAsync(2);

            var (function, handlerMock) = BuildFunction(detectionCounterMock: detectionCounterMock, stateMock: stateMock);

            // Act
            var input = new List<JsonElement> { MakeDetection(LocationId, NodeName, reviewed: false) };
            await function.ProcessDocumentsAsync(input, LocationId, AppNotificationUrl,
                NotificationPeriodMinutes, DetectionPeriodMinutes);

            // Assert - verify the notification was sent and the state store was updated
            handlerMock.Protected().Verify(
                "SendAsync",
                Times.Once(),
                ItExpr.IsAny<HttpRequestMessage>(),
                ItExpr.IsAny<CancellationToken>());
            stateMock.Verify(x => x.UpdateLastNotificationTimeAsync(LocationId), Times.Once);
        }
    }
}
