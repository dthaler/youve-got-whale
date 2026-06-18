// SPDX-License-Identifier: MIT
using Amazon.SimpleEmail;
using Amazon.SimpleEmail.Model;
using Moq;
using NotificationFunction;
using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace NotificationFunction.Tests.Integration
{
    /// <summary>
    /// Integration tests for <see cref="SendNotificationEmail.ProcessDocumentsAsync"/>.
    /// These tests exercise the full notification pipeline with real in-process
    /// logic, but replace the three external I/O boundaries
    /// (<see cref="IAmazonSimpleEmailService"/>, <see cref="IDetectionCounter"/>,
    /// and <see cref="INotificationStateStore"/>) with Moq mocks so the suite
    /// runs without any cloud infrastructure.
    /// </summary>
    public class SendNotificationEmailIntegrationTests
    {
        private const string LocationId = "rpi_orcasound_lab";
        private const string NodeName = "Orcasound Lab";
        private const string RecipientEmail = "recipient@example.com";
        private const string SenderEmail = "sender@example.com";
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

        private static (SendNotificationEmail function,
                         Mock<IAmazonSimpleEmailService> sesMock,
                         Mock<IDetectionCounter> detectionCounterMock,
                         Mock<INotificationStateStore> stateMock)
            BuildFunction(
                int recentDetections = 2,
                DateTime? lastNotificationTime = null)
        {
            var sesMock = new Mock<IAmazonSimpleEmailService>();
            sesMock.Setup(x => x.SendEmailAsync(It.IsAny<SendEmailRequest>(), It.IsAny<CancellationToken>()))
                   .ReturnsAsync(new SendEmailResponse());

            var detectionCounterMock = new Mock<IDetectionCounter>();
            detectionCounterMock.Setup(x => x.CountRecentAsync(It.IsAny<string>(), It.IsAny<int>()))
                                 .ReturnsAsync(recentDetections);

            var stateMock = new Mock<INotificationStateStore>();
            stateMock.Setup(x => x.GetLastNotificationTimeAsync(It.IsAny<string>()))
                     .ReturnsAsync(lastNotificationTime);

            var loggerMock = new Mock<Microsoft.Extensions.Logging.ILogger<SendNotificationEmail>>();
            var function = new SendNotificationEmail(
                loggerMock.Object, sesMock.Object, detectionCounterMock.Object, stateMock.Object);

            return (function, sesMock, detectionCounterMock, stateMock);
        }

        // ---------------------------------------------------------------------------
        // Tests: happy path
        // ---------------------------------------------------------------------------

        /// <summary>
        /// End-to-end: a detection for the configured location triggers
        /// an email with the correct sender, recipient, and body.
        /// </summary>
        [Fact]
        public async Task ProcessDocumentsAsync_SendsEmail_ForDetection()
        {
            var (function, sesMock, _, stateMock) = BuildFunction();

            var input = new List<JsonElement>
            {
                MakeDetection(LocationId, NodeName, Comments)
            };

            await function.ProcessDocumentsAsync(input, LocationId, RecipientEmail, SenderEmail,
                NotificationPeriodMinutes, DetectionPeriodMinutes);

            sesMock.Verify(
                x => x.SendEmailAsync(
                    It.Is<SendEmailRequest>(r =>
                        r.Source == SenderEmail &&
                        r.Destination.ToAddresses.Contains(RecipientEmail) &&
                        r.Message.Body.Html.Data.Contains(NodeName)),
                    It.IsAny<CancellationToken>()),
                Times.Once);

            stateMock.Verify(x => x.UpdateLastNotificationTimeAsync(LocationId), Times.Once);
        }

        /// <summary>
        /// When the reviewed field is absent the detection is treated as unreviewed.
        /// </summary>
        [Fact]
        public async Task ProcessDocumentsAsync_SendsEmail_WhenReviewedFieldMissing()
        {
            var (function, sesMock, _, _) = BuildFunction();

            // No "reviewed" property in the document.
            var input = new List<JsonElement> { MakeDetection(LocationId, NodeName, Comments) };

            await function.ProcessDocumentsAsync(input, LocationId, RecipientEmail, SenderEmail,
                NotificationPeriodMinutes, DetectionPeriodMinutes);

            sesMock.Verify(
                x => x.SendEmailAsync(It.IsAny<SendEmailRequest>(), It.IsAny<CancellationToken>()),
                Times.Once);
        }

        // ---------------------------------------------------------------------------
        // Tests: suppression – rate limiting
        // ---------------------------------------------------------------------------

        /// <summary>
        /// A notification was sent recently; rate limiting prevents another one.
        /// </summary>
        [Fact]
        public async Task ProcessDocumentsAsync_DoesNotSendEmail_WhenRateLimited()
        {
            // Last notification 10 minutes ago; period is 60 minutes.
            var (function, sesMock, _, _) = BuildFunction(lastNotificationTime: DateTime.UtcNow.AddMinutes(-10));

            var input = new List<JsonElement> { MakeDetection(LocationId, NodeName, Comments, reviewed: false) };

            await function.ProcessDocumentsAsync(input, LocationId, RecipientEmail, SenderEmail,
                NotificationPeriodMinutes, DetectionPeriodMinutes);

            sesMock.Verify(
                x => x.SendEmailAsync(It.IsAny<SendEmailRequest>(), It.IsAny<CancellationToken>()),
                Times.Never);
        }

        /// <summary>
        /// The rate-limit window has passed; an email should be sent.
        /// </summary>
        [Fact]
        public async Task ProcessDocumentsAsync_SendsEmail_WhenRateLimitWindowHasExpired()
        {
            // Last notification 90 minutes ago; period is 60 minutes.
            var (function, sesMock, _, _) = BuildFunction(lastNotificationTime: DateTime.UtcNow.AddMinutes(-90));

            var input = new List<JsonElement> { MakeDetection(LocationId, NodeName, Comments, reviewed: false) };

            await function.ProcessDocumentsAsync(input, LocationId, RecipientEmail, SenderEmail,
                NotificationPeriodMinutes, DetectionPeriodMinutes);

            sesMock.Verify(
                x => x.SendEmailAsync(It.IsAny<SendEmailRequest>(), It.IsAny<CancellationToken>()),
                Times.Once);
        }

        // ---------------------------------------------------------------------------
        // Tests: suppression – insufficient detections
        // ---------------------------------------------------------------------------

        /// <summary>
        /// Only one recent detection in the window; the two-detection threshold is not met.
        /// </summary>
        [Fact]
        public async Task ProcessDocumentsAsync_DoesNotSendEmail_WhenInsufficientRecentDetections()
        {
            var (function, sesMock, _, _) = BuildFunction(recentDetections: 1);

            var input = new List<JsonElement> { MakeDetection(LocationId, NodeName, Comments, reviewed: false) };

            await function.ProcessDocumentsAsync(input, LocationId, RecipientEmail, SenderEmail,
                NotificationPeriodMinutes, DetectionPeriodMinutes);

            sesMock.Verify(
                x => x.SendEmailAsync(It.IsAny<SendEmailRequest>(), It.IsAny<CancellationToken>()),
                Times.Never);
        }

        // ---------------------------------------------------------------------------
        // Tests: email content
        // ---------------------------------------------------------------------------

        /// <summary>
        /// The email body is HTML and contains the detection count and node name.
        /// </summary>
        [Fact]
        public async Task ProcessDocumentsAsync_EmailBody_ContainsNodeNameAndDetectionCount()
        {
            SendEmailRequest? capturedRequest = null;
            var sesMock = new Mock<IAmazonSimpleEmailService>();
            sesMock.Setup(x => x.SendEmailAsync(It.IsAny<SendEmailRequest>(), It.IsAny<CancellationToken>()))
                   .Callback<SendEmailRequest, CancellationToken>((req, _) => capturedRequest = req)
                   .ReturnsAsync(new SendEmailResponse());

            var detectionCounterMock = new Mock<IDetectionCounter>();
            detectionCounterMock.Setup(x => x.CountRecentAsync(LocationId, DetectionPeriodMinutes))
                                 .ReturnsAsync(4);

            var stateMock = new Mock<INotificationStateStore>();
            stateMock.Setup(x => x.GetLastNotificationTimeAsync(LocationId))
                     .ReturnsAsync((DateTime?)null);

            var loggerMock = new Mock<Microsoft.Extensions.Logging.ILogger<SendNotificationEmail>>();
            var function = new SendNotificationEmail(
                loggerMock.Object, sesMock.Object, detectionCounterMock.Object, stateMock.Object);

            var input = new List<JsonElement> { MakeDetection(LocationId, NodeName, Comments, reviewed: false) };
            await function.ProcessDocumentsAsync(input, LocationId, RecipientEmail, SenderEmail,
                NotificationPeriodMinutes, DetectionPeriodMinutes);

            Assert.NotNull(capturedRequest);
            string body = capturedRequest!.Message.Body.Html.Data;
            Assert.Contains(NodeName, body);
            Assert.Contains("4", body);
            Assert.Contains("resident", body);
            Assert.Contains("UTF-8", capturedRequest.Message.Body.Html.Charset);
        }
    }
}
