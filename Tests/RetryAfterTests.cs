using System.Net;
using Azure;
using Azure.Core;
using FDR11.Notifications.Email.Concretes;
using FDR11.Notifications.Email.Models;
using FDR11.Notifications.Email.Options;
using FDR11.Notifications.Email.Resilience;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SendGrid;

namespace FDR11.Notifications.Email.Tests;

public class RetryAfterTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("0", 0)]
    [InlineData("60", 60)]
    [InlineData(" 120 ", 120)]
    [InlineData("Tue, 20 Oct 2026 12:01:00 GMT", 60)]
    [InlineData("Tue, 20 Oct 2026 11:59:00 GMT", 0)]
    public void ParsesSecondsAndDates(string header, int seconds)
        => Assert.Equal(TimeSpan.FromSeconds(seconds), EmailRetry.ParseRetryAfter(header, Now));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("-1")]
    [InlineData("1.5")]
    [InlineData("invalid")]
    [InlineData("60, 120")]
    public void InvalidHeadersUseDefaultBackoff(string? header)
        => Assert.Null(EmailRetry.ParseRetryAfter(header, Now));

    [Theory]
    [InlineData("999999999999999999999999999999999")]
    [InlineData("4294968")]
    public async Task UnrepresentableWaitDoesNotRetryEarly(string header)
    {
        var error = new HttpRequestException("limited", null, HttpStatusCode.TooManyRequests);
        error.Data[EmailRetry.RetryAfterKey] = header;
        var calls = 0;
        var actual = await Assert.ThrowsAsync<HttpRequestException>(() =>
            EmailRetry.ExecuteAsync(new() { MaxRetryAttempts = 2 }, "SendGrid", NullLogger.Instance,
                _ => { calls++; throw error; }, TestContext.Current.CancellationToken));
        Assert.Same(error, actual);
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("invalid")]
    [InlineData(null)]
    public async Task SendGridRetriesWithZeroOrFallbackDelay(string? header)
    {
        using var handler = new HeaderHandler(header);
        using var http = new HttpClient(handler);
        var sender = new Sender(new SendGridClient(http, "key"), NullLogger<SendGridEmailSender>.Instance);
        await sender.SendAsync(Message(), TestContext.Current.CancellationToken);
        Assert.Equal(2, handler.Calls);
    }

    [Fact]
    public async Task SendGridPreservesServerDelayAndCancellation()
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        using var handler = new HeaderHandler("60");
        using var http = new HttpClient(handler);
        var logger = new RetryLogger(cts.Cancel);
        var sender = new Sender(new SendGridClient(http, "key"), logger);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sender.SendAsync(Message(), cts.Token));
        Assert.Equal(60, logger.Delay);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task AzureResponseHeaderReachesPolicy()
    {
        using var response = new HeaderResponse("60");
        var error = new RequestFailedException(response);
        Assert.Equal(TimeSpan.FromSeconds(60), EmailRetry.GetRetryAfter(error, Now));
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var logger = new RetryLogger(cts.Cancel);
        var calls = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => EmailRetry.ExecuteAsync(
            new() { MaxRetryAttempts = 2, BaseDelay = TimeSpan.Zero }, "Azure", logger,
            _ => { calls++; throw error; }, cts.Token));
        Assert.Equal(60, logger.Delay);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task HeaderDoesNotMakePermanentFailureRetryable()
    {
        using var handler = new HeaderHandler("60", HttpStatusCode.Unauthorized);
        using var http = new HttpClient(handler);
        var sender = new Sender(new SendGridClient(http, "key"), NullLogger<SendGridEmailSender>.Instance);
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => sender.SendAsync(Message(), TestContext.Current.CancellationToken));
        Assert.Equal(HttpStatusCode.Unauthorized, error.StatusCode);
        Assert.Equal(1, handler.Calls);
    }

    private static EmailMessage Message() => new()
    {
        From = "from@example.test", To = ["to@example.test"], Subject = "subject", Body = "body"
    };

    private sealed class Sender(ISendGridClient client, ILogger<SendGridEmailSender> logger) : SendGridEmailSender(
        Microsoft.Extensions.Options.Options.Create(new EmailOptions
        { Retry = new() { MaxRetryAttempts = 2, BaseDelay = TimeSpan.Zero } }), logger)
    {
        protected override ISendGridClient CreateClient() => client;
    }

    private sealed class HeaderHandler(string? header, HttpStatusCode status = HttpStatusCode.TooManyRequests) : HttpMessageHandler
    {
        public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(++Calls == 1 ? status : HttpStatusCode.Accepted) { Content = new StringContent("") };
            if (header is not null) response.Headers.TryAddWithoutValidation("Retry-After", header);
            return Task.FromResult(response);
        }
    }

    private sealed class RetryLogger(Action cancel) : ILogger<SendGridEmailSender>
    {
        public double? Delay;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => true;
        public void Log<TState>(LogLevel level, EventId id, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (id.Id != 1002) return;
            Delay = (double)((IEnumerable<KeyValuePair<string, object?>>)state!).Single(x => x.Key == "DelaySeconds").Value!;
            cancel();
        }
    }

    private sealed class HeaderResponse(string header) : Azure.Response
    {
        public override int Status => 429;
        public override string ReasonPhrase => "Too Many Requests";
        public override Stream? ContentStream { get; set; }
        public override string ClientRequestId { get; set; } = "";
        public override void Dispose() { }
        protected override bool ContainsHeader(string name) => name.Equals("Retry-After", StringComparison.OrdinalIgnoreCase);
        protected override bool TryGetHeader(string name, out string value) { value = header; return ContainsHeader(name); }
        protected override bool TryGetHeaderValues(string name, out IEnumerable<string> values) { values = [header]; return ContainsHeader(name); }
        protected override IEnumerable<HttpHeader> EnumerateHeaders() => [new("Retry-After", header)];
    }
}
