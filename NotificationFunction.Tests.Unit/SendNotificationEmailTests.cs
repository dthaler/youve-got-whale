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

namespace NotificationFunction.Tests.Unit
{
    /// <summary>
    /// Unit tests for the document-matching and notification-suppression logic in
    /// <see cref="SendNotificationEmail.ProcessDocumentsAsync"/>.
    /// All external dependencies (SES, detection counter, state store) are mocked so
    /// these tests run without any network or cloud connections.
    /// </summary>
    public class SendNotificationEmailTests
    {
        private const string LocationId = "rpi_orcasound_lab";
        private const string NodeName = "Orcasound Lab";
        private const string RecipientEmail = "recipient@example.com";
        private const string SenderEmail = "sender@example.com";
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

        private static SendNotificationEmail BuildFunction(
            Mock<IAmazonSimpleEmailService>? sesMock = null,
            Mock<IDetectionCounter>? detectionCounterMock = null,
            Mock<INotificationStateStore>? stateMock = null)
        {
            sesMock ??= new Mock<IAmazonSimpleEmailService>();
            detectionCounterMock ??= new Mock<IDetectionCounter>();
            stateMock ??= new Mock<INotificationStateStore>();

            var loggerMock = new Mock<Microsoft.Extensions.Logging.ILogger<SendNotificationEmail>>();
            return new SendNotificationEmail(
                loggerMock.Object,
                sesMock.Object,
                detectionCounterMock.Object,
                stateMock.Object);
        }

        // ---------------------------------------------------------------------------
        // Tests: document matching
        // ---------------------------------------------------------------------------

        [Fact]
        public async Task ProcessDocumentsAsync_SendsEmail_ForDetection()
        {
            var sesMock = new Mock<IAmazonSimpleEmailService>();
            sesMock.Setup(x => x.SendEmailAsync(It.IsAny<Amazon.SimpleEmail.Model.SendEmailRequest>(), default))
                   .ReturnsAsync(new Amazon.SimpleEmail.Model.SendEmailResponse());

            var detectionCounterMock = new Mock<IDetectionCounter>();
            detectionCounterMock.Setup(x => x.CountRecentAsync(LocationId, DetectionPeriodMinutes))
                                 .ReturnsAsync(3);

            var stateMock = new Mock<INotificationStateStore>();
            stateMock.Setup(x => x.GetLastNotificationTimeAsync(LocationId))
                     .ReturnsAsync((DateTime?)null);

            var function = BuildFunction(sesMock, detectionCounterMock, stateMock);

            var input = new List<JsonElement> { MakeDetection(LocationId, NodeName, reviewed: false) };
            await function.ProcessDocumentsAsync(input, LocationId, RecipientEmail, SenderEmail,
                NotificationPeriodMinutes, DetectionPeriodMinutes);

            sesMock.Verify(
                x => x.SendEmailAsync(
                    It.Is<Amazon.SimpleEmail.Model.SendEmailRequest>(r =>
                        r.Source == SenderEmail &&
                        r.Destination.ToAddresses.Contains(RecipientEmail) &&
                        r.Message.Body.Html.Data.Contains(NodeName)),
                    default),
                Times.Once);
            stateMock.Verify(x => x.UpdateLastNotificationTimeAsync(LocationId), Times.Once);
        }

        [Fact]
        public async Task ProcessDocumentsAsync_SendsEmail_WhenReviewedFieldMissing()
        {
            var sesMock = new Mock<IAmazonSimpleEmailService>();
            sesMock.Setup(x => x.SendEmailAsync(It.IsAny<Amazon.SimpleEmail.Model.SendEmailRequest>(), default))
                   .ReturnsAsync(new Amazon.SimpleEmail.Model.SendEmailResponse());

            var detectionCounterMock = new Mock<IDetectionCounter>();
            detectionCounterMock.Setup(x => x.CountRecentAsync(LocationId, DetectionPeriodMinutes))
                                 .ReturnsAsync(2);

            var stateMock = new Mock<INotificationStateStore>();
            stateMock.Setup(x => x.GetLastNotificationTimeAsync(LocationId))
                     .ReturnsAsync((DateTime?)null);

            var function = BuildFunction(sesMock, detectionCounterMock, stateMock);

            // Document has no "reviewed" field – should be treated as unreviewed.
            var input = new List<JsonElement> { MakeDetection(LocationId, NodeName) };
            await function.ProcessDocumentsAsync(input, LocationId, RecipientEmail, SenderEmail,
                NotificationPeriodMinutes, DetectionPeriodMinutes);

            sesMock.Verify(
                x => x.SendEmailAsync(It.IsAny<Amazon.SimpleEmail.Model.SendEmailRequest>(), default),
                Times.Once);
        }

        [Fact]
        public async Task ProcessDocumentsAsync_DoesNotSendEmail_WhenNoMatchingLocation()
        {
            var sesMock = new Mock<IAmazonSimpleEmailService>();
            var function = BuildFunction(sesMock);

            // Document belongs to a different location.
            var input = new List<JsonElement> { MakeDetection("rpi_other_location", "Other Location") };
            await function.ProcessDocumentsAsync(input, LocationId, RecipientEmail, SenderEmail,
                NotificationPeriodMinutes, DetectionPeriodMinutes);

            sesMock.Verify(
                x => x.SendEmailAsync(It.IsAny<Amazon.SimpleEmail.Model.SendEmailRequest>(), default),
                Times.Never);
        }

        // ---------------------------------------------------------------------------
        // Tests: rate limiting
        // ---------------------------------------------------------------------------

        [Fact]
        public async Task ProcessDocumentsAsync_DoesNotSendEmail_WhenRateLimited()
        {
            var sesMock = new Mock<IAmazonSimpleEmailService>();

            var stateMock = new Mock<INotificationStateStore>();
            // Last notification was 10 minutes ago; limit is 60 minutes.
            stateMock.Setup(x => x.GetLastNotificationTimeAsync(LocationId))
                     .ReturnsAsync(DateTime.UtcNow.AddMinutes(-10));

            var function = BuildFunction(sesMock, stateMock: stateMock);

            var input = new List<JsonElement> { MakeDetection(LocationId, NodeName, reviewed: false) };
            await function.ProcessDocumentsAsync(input, LocationId, RecipientEmail, SenderEmail,
                NotificationPeriodMinutes, DetectionPeriodMinutes);

            sesMock.Verify(
                x => x.SendEmailAsync(It.IsAny<Amazon.SimpleEmail.Model.SendEmailRequest>(), default),
                Times.Never);
        }

        [Fact]
        public async Task ProcessDocumentsAsync_SendsEmail_WhenRateLimitHasExpired()
        {
            var sesMock = new Mock<IAmazonSimpleEmailService>();
            sesMock.Setup(x => x.SendEmailAsync(It.IsAny<Amazon.SimpleEmail.Model.SendEmailRequest>(), default))
                   .ReturnsAsync(new Amazon.SimpleEmail.Model.SendEmailResponse());

            var detectionCounterMock = new Mock<IDetectionCounter>();
            detectionCounterMock.Setup(x => x.CountRecentAsync(LocationId, DetectionPeriodMinutes))
                                 .ReturnsAsync(2);

            var stateMock = new Mock<INotificationStateStore>();
            // Last notification was 90 minutes ago; limit is 60 minutes – should proceed.
            stateMock.Setup(x => x.GetLastNotificationTimeAsync(LocationId))
                     .ReturnsAsync(DateTime.UtcNow.AddMinutes(-90));

            var function = BuildFunction(sesMock, detectionCounterMock, stateMock);

            var input = new List<JsonElement> { MakeDetection(LocationId, NodeName, reviewed: false) };
            await function.ProcessDocumentsAsync(input, LocationId, RecipientEmail, SenderEmail,
                NotificationPeriodMinutes, DetectionPeriodMinutes);

            sesMock.Verify(
                x => x.SendEmailAsync(It.IsAny<Amazon.SimpleEmail.Model.SendEmailRequest>(), default),
                Times.Once);
        }

        // ---------------------------------------------------------------------------
        // Tests: detection cluster threshold
        // ---------------------------------------------------------------------------

        [Fact]
        public async Task ProcessDocumentsAsync_DoesNotSendEmail_WhenInsufficientRecentDetections()
        {
            var sesMock = new Mock<IAmazonSimpleEmailService>();

            var detectionCounterMock = new Mock<IDetectionCounter>();
            // Only 1 recent detection – threshold requires at least 2.
            detectionCounterMock.Setup(x => x.CountRecentAsync(LocationId, DetectionPeriodMinutes))
                                 .ReturnsAsync(1);

            var stateMock = new Mock<INotificationStateStore>();
            stateMock.Setup(x => x.GetLastNotificationTimeAsync(LocationId))
                     .ReturnsAsync((DateTime?)null);

            var function = BuildFunction(sesMock, detectionCounterMock, stateMock);

            var input = new List<JsonElement> { MakeDetection(LocationId, NodeName, reviewed: false) };
            await function.ProcessDocumentsAsync(input, LocationId, RecipientEmail, SenderEmail,
                NotificationPeriodMinutes, DetectionPeriodMinutes);

            sesMock.Verify(
                x => x.SendEmailAsync(It.IsAny<Amazon.SimpleEmail.Model.SendEmailRequest>(), default),
                Times.Never);
        }

        [Fact]
        public async Task ProcessDocumentsAsync_SendsEmail_WhenDetectionCountMeetsThreshold()
        {
            var sesMock = new Mock<IAmazonSimpleEmailService>();
            sesMock.Setup(x => x.SendEmailAsync(It.IsAny<Amazon.SimpleEmail.Model.SendEmailRequest>(), default))
                   .ReturnsAsync(new Amazon.SimpleEmail.Model.SendEmailResponse());

            var detectionCounterMock = new Mock<IDetectionCounter>();
            detectionCounterMock.Setup(x => x.CountRecentAsync(LocationId, DetectionPeriodMinutes))
                                 .ReturnsAsync(2);

            var stateMock = new Mock<INotificationStateStore>();
            stateMock.Setup(x => x.GetLastNotificationTimeAsync(LocationId))
                     .ReturnsAsync((DateTime?)null);

            var function = BuildFunction(sesMock, detectionCounterMock, stateMock);

            var input = new List<JsonElement> { MakeDetection(LocationId, NodeName, reviewed: false) };
            await function.ProcessDocumentsAsync(input, LocationId, RecipientEmail, SenderEmail,
                NotificationPeriodMinutes, DetectionPeriodMinutes);

            sesMock.Verify(
                x => x.SendEmailAsync(It.IsAny<Amazon.SimpleEmail.Model.SendEmailRequest>(), default),
                Times.Once);
        }

        // ---------------------------------------------------------------------------
        // Tests: email content
        // ---------------------------------------------------------------------------

        [Fact]
        public async Task ProcessDocumentsAsync_EmailBodyContainsNodeName()
        {
            Amazon.SimpleEmail.Model.SendEmailRequest? capturedRequest = null;
            var sesMock = new Mock<IAmazonSimpleEmailService>();
            sesMock.Setup(x => x.SendEmailAsync(It.IsAny<Amazon.SimpleEmail.Model.SendEmailRequest>(), default))
                   .Callback<Amazon.SimpleEmail.Model.SendEmailRequest, System.Threading.CancellationToken>(
                       (req, _) => capturedRequest = req)
                   .ReturnsAsync(new Amazon.SimpleEmail.Model.SendEmailResponse());

            var detectionCounterMock = new Mock<IDetectionCounter>();
            detectionCounterMock.Setup(x => x.CountRecentAsync(LocationId, DetectionPeriodMinutes))
                                 .ReturnsAsync(3);

            var stateMock = new Mock<INotificationStateStore>();
            stateMock.Setup(x => x.GetLastNotificationTimeAsync(LocationId))
                     .ReturnsAsync((DateTime?)null);

            var function = BuildFunction(sesMock, detectionCounterMock, stateMock);

            var input = new List<JsonElement> { MakeDetection(LocationId, NodeName, reviewed: false) };
            await function.ProcessDocumentsAsync(input, LocationId, RecipientEmail, SenderEmail,
                NotificationPeriodMinutes, DetectionPeriodMinutes);

            Assert.NotNull(capturedRequest);
            Assert.Contains(NodeName, capturedRequest!.Message.Body.Html.Data);
        }

        [Fact]
        public async Task ProcessDocumentsAsync_EmailBodyContainsDetectionCount()
        {
            Amazon.SimpleEmail.Model.SendEmailRequest? capturedRequest = null;
            var sesMock = new Mock<IAmazonSimpleEmailService>();
            sesMock.Setup(x => x.SendEmailAsync(It.IsAny<Amazon.SimpleEmail.Model.SendEmailRequest>(), default))
                   .Callback<Amazon.SimpleEmail.Model.SendEmailRequest, System.Threading.CancellationToken>(
                       (req, _) => capturedRequest = req)
                   .ReturnsAsync(new Amazon.SimpleEmail.Model.SendEmailResponse());

            var detectionCounterMock = new Mock<IDetectionCounter>();
            detectionCounterMock.Setup(x => x.CountRecentAsync(LocationId, DetectionPeriodMinutes))
                                 .ReturnsAsync(5);

            var stateMock = new Mock<INotificationStateStore>();
            stateMock.Setup(x => x.GetLastNotificationTimeAsync(LocationId))
                     .ReturnsAsync((DateTime?)null);

            var function = BuildFunction(sesMock, detectionCounterMock, stateMock);

            var input = new List<JsonElement> { MakeDetection(LocationId, NodeName, reviewed: false) };
            await function.ProcessDocumentsAsync(input, LocationId, RecipientEmail, SenderEmail,
                NotificationPeriodMinutes, DetectionPeriodMinutes);

            Assert.NotNull(capturedRequest);
            Assert.Contains("5", capturedRequest!.Message.Body.Html.Data);
        }

        // ---------------------------------------------------------------------------
        // Tests: multiple documents in one batch
        // ---------------------------------------------------------------------------

        [Fact]
        public async Task ProcessDocumentsAsync_SendsEmail_WhenMatchingDocIsNotFirst()
        {
            var sesMock = new Mock<IAmazonSimpleEmailService>();
            sesMock.Setup(x => x.SendEmailAsync(It.IsAny<Amazon.SimpleEmail.Model.SendEmailRequest>(), default))
                   .ReturnsAsync(new Amazon.SimpleEmail.Model.SendEmailResponse());

            var detectionCounterMock = new Mock<IDetectionCounter>();
            detectionCounterMock.Setup(x => x.CountRecentAsync(LocationId, DetectionPeriodMinutes))
                                 .ReturnsAsync(2);

            var stateMock = new Mock<INotificationStateStore>();
            stateMock.Setup(x => x.GetLastNotificationTimeAsync(LocationId))
                     .ReturnsAsync((DateTime?)null);

            var function = BuildFunction(sesMock, detectionCounterMock, stateMock);

            var input = new List<JsonElement>
            {
                MakeDetection("rpi_other", "Other Node", reviewed: false),
                MakeDetection(LocationId, NodeName, reviewed: false),
            };

            await function.ProcessDocumentsAsync(input, LocationId, RecipientEmail, SenderEmail,
                NotificationPeriodMinutes, DetectionPeriodMinutes);

            sesMock.Verify(
                x => x.SendEmailAsync(It.IsAny<Amazon.SimpleEmail.Model.SendEmailRequest>(), default),
                Times.Once);
        }

        [Fact]
        public async Task ProcessDocumentsAsync_DoesNotSendEmail_WhenMatchingDocIsReviewedAndOtherIsNot()
        {
            // The reviewed matching doc should be skipped; unreviewed non-matching doc should not trigger.
            var sesMock = new Mock<IAmazonSimpleEmailService>();
            var function = BuildFunction(sesMock);

            var input = new List<JsonElement>
            {
                MakeDetection(LocationId, NodeName, reviewed: true),
                MakeDetection("rpi_other", "Other Node", reviewed: false),
            };

            await function.ProcessDocumentsAsync(input, LocationId, RecipientEmail, SenderEmail,
                NotificationPeriodMinutes, DetectionPeriodMinutes);

            sesMock.Verify(
                x => x.SendEmailAsync(It.IsAny<Amazon.SimpleEmail.Model.SendEmailRequest>(), default),
                Times.Never);
        }
    }
}
