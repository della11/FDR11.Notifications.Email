using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Globalization;
using Amazon.SimpleEmail;
using Azure;
using MailKit.Net.Smtp;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using FDR11.Notifications.Email.Options;
using Polly;
using Polly.Retry;

namespace FDR11.Notifications.Email.Resilience;

internal static class EmailRetry
{
    internal const string RetryAfterKey = "FDR11.Notifications.Email.RetryAfter";
    // System.Threading timers cannot represent longer waits; never shorten a server deadline.
    private static readonly TimeSpan MaximumTimerDelay = TimeSpan.FromMilliseconds(uint.MaxValue - 1);
    internal static Task ExecuteAsync(EmailRetryOptions options, string provider, ILogger logger,
        Func<CancellationToken, Task> send, CancellationToken cancellationToken)
    {
        Validate(options);
        if (options.MaxRetryAttempts == 0) return send(cancellationToken);
        var pipeline = new ResiliencePipelineBuilder().AddRetry(new RetryStrategyOptions
        {
            MaxRetryAttempts = options.MaxRetryAttempts,
            Delay = options.BaseDelay,
            BackoffType = DelayBackoffType.Exponential,
            UseJitter = true,
            MaxDelay = TimeSpan.FromSeconds(30),
            ShouldHandle = args => ValueTask.FromResult(!args.Context.CancellationToken.IsCancellationRequested &&
                CanRetry(provider, args.Outcome.Exception) &&
                !(GetRetryAfter(args.Outcome.Exception, DateTimeOffset.UtcNow) > MaximumTimerDelay)),
            DelayGenerator = args => ValueTask.FromResult(GetRetryAfter(args.Outcome.Exception, DateTimeOffset.UtcNow)),
            OnRetry = args =>
            {
                logger.LogWarning(new EventId(1002, "EmailSendRetry"),
                    "Email retry: {Provider} {RetryNumber} {DelaySeconds} {TraceId}",
                    provider, args.AttemptNumber + 1, args.RetryDelay.TotalSeconds, Activity.Current?.TraceId.ToString());
                Activity.Current?.AddEvent(new ActivityEvent("email.retry", tags: new ActivityTagsCollection
                {
                    { "email.retry.number", args.AttemptNumber + 1 }, { "email.retry.delay", args.RetryDelay.TotalSeconds }
                }));
                return default;
            }
        }).Build();
        return pipeline.ExecuteAsync(async token => await send(token).ConfigureAwait(false), cancellationToken).AsTask();
    }

    internal static TimeSpan? GetRetryAfter(Exception? error, DateTimeOffset now)
    {
        string? header = error?.Data[RetryAfterKey] as string;
        if (error is RequestFailedException azure &&
            azure.GetRawResponse()?.Headers.TryGetValue("Retry-After", out var value) == true)
            header = value;
        return ParseRetryAfter(header, now);
    }

    internal static TimeSpan? ParseRetryAfter(string? header, DateTimeOffset now)
    {
        header = header?.Trim();
        if (string.IsNullOrEmpty(header)) return null;
        if (header.All(c => c is >= '0' and <= '9'))
        {
            if (!ulong.TryParse(header, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds) ||
                seconds > (ulong)MaximumTimerDelay.TotalSeconds)
                return TimeSpan.MaxValue;
            return TimeSpan.FromSeconds(seconds);
        }
        if (!RetryConditionHeaderValue.TryParse(header, out var parsed) || parsed.Date is not { } date)
            return null;
        return date > now ? date - now : TimeSpan.Zero;
    }

    internal static bool CanRetry(string provider, Exception? error) => (provider, error) switch
    {
        ("SendGrid", HttpRequestException { StatusCode: HttpStatusCode.TooManyRequests }) => true,
        ("Azure", RequestFailedException { Status: 429 }) => true,
        ("SES", AmazonSimpleEmailServiceException { ErrorCode: "Throttling" or "ThrottlingException" or "TooManyRequestsException" }) => true,
        ("SMTP", SmtpCommandException smtp) => (int)smtp.StatusCode is >= 400 and < 500 &&
            smtp.ErrorCode is SmtpErrorCode.SenderNotAccepted or SmtpErrorCode.RecipientNotAccepted or SmtpErrorCode.MessageNotAccepted,
        _ => false
    };

    internal static void Validate(EmailRetryOptions? options)
    {
        if (options is null || options.MaxRetryAttempts is < 0 or > 10 || options.BaseDelay < TimeSpan.Zero || options.BaseDelay > TimeSpan.FromSeconds(30))
            throw new OptionsValidationException(nameof(EmailOptions), typeof(EmailOptions),
                ["EmailOptions:Retry richiede MaxRetryAttempts tra 0 e 10 e BaseDelay tra 0 e 30 secondi."]);
    }
}
