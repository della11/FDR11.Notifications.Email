using System.Net;
using Amazon.SimpleEmail;
using Amazon;
using Amazon.SimpleEmail.Model;
using Azure;
using Azure.Communication.Email;
using FDR11.Notifications.Email.Concretes;
using FDR11.Notifications.Email.Options;
using FDR11.Notifications.Email.Resilience;
using MailKit.Net.Smtp;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SendGrid;
using EmailMessage = FDR11.Notifications.Email.Models.EmailMessage;

namespace FDR11.Notifications.Email.Tests;

public class RetryTests
{
    private static EmailRetryOptions Retry(int count = 2) => new() { MaxRetryAttempts = count, BaseDelay = TimeSpan.Zero };
    private static IOptions<EmailOptions> Options() => Microsoft.Extensions.Options.Options.Create(new EmailOptions { Retry = Retry() });
    private static EmailMessage Message() => new() { From = "from@example.test", To = ["to@example.test"], Subject = "subject", Body = "body" };

    [Theory]
    [InlineData(0, 1)]
    [InlineData(2, 3)]
    public async Task LimitIncludesOriginalAttempt(int retries, int expected)
    {
        var count = 0;
        var error = new HttpRequestException("private", null, HttpStatusCode.TooManyRequests);
        var actual = await Assert.ThrowsAsync<HttpRequestException>(() => EmailRetry.ExecuteAsync(Retry(retries), "SendGrid", NullLogger.Instance,
            _ => { count++; throw error; }, TestContext.Current.CancellationToken));
        Assert.Same(error, actual);
        Assert.Equal(expected, count);
    }

    [Fact]
    public async Task CancellationDuringBackoffStopsFurtherAttempts()
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var count = 0;
        var task = EmailRetry.ExecuteAsync(new() { MaxRetryAttempts = 2, BaseDelay = TimeSpan.FromSeconds(30) }, "SendGrid", NullLogger.Instance,
            _ => { count++; throw new HttpRequestException("private", null, HttpStatusCode.TooManyRequests); }, cts.Token);
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.Equal(1, count);
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(11, 0)]
    [InlineData(1, -1)]
    [InlineData(1, 31)]
    public void InvalidConfigurationFailsValidation(int count, int delay)
    {
        var result = new EmailOptionsValidator().Validate(null, new EmailOptions
        {
            Provider = "SendGrid", SendGridApiKey = "key", Retry = new() { MaxRetryAttempts = count, BaseDelay = TimeSpan.FromSeconds(delay) }
        });
        Assert.True(result.Failed);
        Assert.Contains("Retry", result.FailureMessage);
    }

    [Fact]
    public void OnlyExplicitRejectionsAreEligible()
    {
        Assert.True(EmailRetry.CanRetry("SES", new AmazonSimpleEmailServiceException("private") { ErrorCode = "Throttling" }));
        Assert.False(EmailRetry.CanRetry("SES", new AmazonSimpleEmailServiceException("private") { ErrorCode = "MessageRejected" }));
        Assert.True(EmailRetry.CanRetry("SMTP", new SmtpCommandException(SmtpErrorCode.MessageNotAccepted, (SmtpStatusCode)451, "private")));
        Assert.False(EmailRetry.CanRetry("SMTP", new SmtpCommandException(SmtpErrorCode.MessageNotAccepted, (SmtpStatusCode)550, "private")));
        Assert.False(EmailRetry.CanRetry("SMTP", new SmtpCommandException(SmtpErrorCode.UnexpectedStatusCode, (SmtpStatusCode)451, "private")));
        foreach (var provider in new[] { "SMTP", "SES", "Azure", "SendGrid" })
        {
            Assert.False(EmailRetry.CanRetry(provider, new TimeoutException()));
            Assert.False(EmailRetry.CanRetry(provider, new IOException()));
            Assert.False(EmailRetry.CanRetry(provider, new OperationCanceledException()));
        }
    }

    [Theory]
    [InlineData(true, 429, 3)]
    [InlineData(false, 429, 3)]
    [InlineData(false, 500, 1)]
    [InlineData(false, 401, 1)]
    public async Task SendGridRetriesOnlyThrottling(bool sync, int status, int expected)
    {
        using var handler = new SequenceHandler(status);
        using var http = new HttpClient(handler);
        var sender = new GridSender(new SendGridClient(http, "key"));
        Task Send() => sync ? sender.Send(Message()) : sender.SendAsync(Message(), TestContext.Current.CancellationToken);
        if (status == 429) await Send(); else await Assert.ThrowsAsync<HttpRequestException>(Send);
        Assert.Equal(expected, handler.Calls);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task AzureRetriesStartButNeverResendsAfterPollingFailure(bool sync, bool failPolling)
    {
        var client = new AzureClient(failPolling);
        var sender = new AzureSender(client);
        Task Send() => sync ? sender.Send(Message()) : sender.SendAsync(Message(), TestContext.Current.CancellationToken);
        if (failPolling) await Assert.ThrowsAsync<RequestFailedException>(Send); else await Send();
        Assert.Equal(3, client.Calls);
        Assert.Equal(1, client.Operation.Polls);
    }

    private sealed class SequenceHandler(int status) : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(++Calls < 3 ? (HttpStatusCode)status : HttpStatusCode.Accepted)
            { Content = new StringContent("") });
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task SesRetriesBothRequestFormats(bool sync, bool attachments)
    {
        var calls = 0;
        byte[]? firstPayload = null;
        var sender = new SesSender(() => new SesClient(request =>
        {
            if (request is not null)
            {
                Assert.Equal(0, request.RawMessage.Data.Position);
                var payload = request.RawMessage.Data.ToArray();
                Assert.NotEmpty(payload);
                firstPayload ??= payload;
                // MIME boundaries are regenerated; verify attachment bytes through decoding instead.
                using var mime = MimeKit.MimeMessage.Load(new MemoryStream(payload), TestContext.Current.CancellationToken);
                Assert.Single(mime.Attachments);
            }
            if (++calls < 3) throw new AmazonSimpleEmailServiceException("private") { ErrorCode = "Throttling" };
        }));
        var message = Message();
        if (attachments) message.Attachments = [new() { FileName = "a.txt", ContentType = "text/plain", Content = [42] }];
        if (sync) await sender.Send(message); else await sender.SendAsync(message, TestContext.Current.CancellationToken);
        Assert.Equal(3, calls);
        Assert.Equal(attachments, firstPayload is not null);
    }

    private sealed class SesSender(Func<IAmazonSimpleEmailService> factory) : SesEmailSender(Options())
    {
        protected override IAmazonSimpleEmailService CreateClient() => factory();
    }
    private sealed class SesClient(Action<SendRawEmailRequest?> attempt) : AmazonSimpleEmailServiceClient("key", "secret", RegionEndpoint.EUWest1)
    {
        public override Task<SendEmailResponse> SendEmailAsync(SendEmailRequest request, CancellationToken cancellationToken = default)
        {
            attempt(null);
            return Task.FromResult(new SendEmailResponse());
        }
        public override Task<SendRawEmailResponse> SendRawEmailAsync(SendRawEmailRequest request, CancellationToken cancellationToken = default)
        {
            attempt(request);
            return Task.FromResult(new SendRawEmailResponse());
        }
    }
    private sealed class GridSender(ISendGridClient client) : SendGridEmailSender(Options())
    {
        protected override ISendGridClient CreateClient() => client;
    }
    private sealed class AzureSender(EmailClient client) : AzureEmailSender(Options())
    {
        protected override EmailClient CreateClient() => client;
    }
    private sealed class AzureClient(bool failPolling) : EmailClient
    {
        public int Calls;
        public readonly Operation Operation = new(failPolling);
        public override Task<EmailSendOperation> SendAsync(WaitUntil waitUntil, Azure.Communication.Email.EmailMessage message, CancellationToken cancellationToken = default)
        {
            Assert.Equal(WaitUntil.Started, waitUntil);
            if (++Calls < 3) throw new RequestFailedException(429, "private");
            return Task.FromResult<EmailSendOperation>(Operation);
        }
    }
    private sealed class Operation(bool failPolling) : EmailSendOperation
    {
        public int Polls;
        public override ValueTask<Response<EmailSendResult>> WaitForCompletionAsync(CancellationToken cancellationToken = default)
        {
            Polls++;
            if (failPolling) throw new RequestFailedException(429, "polling failure");
            return ValueTask.FromResult<Response<EmailSendResult>>(null!);
        }
    }
}
