using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.Logging;

namespace FDR11.Notifications.Email.Diagnostics;

/// <summary>Nomi delle sorgenti da abilitare nel collector di tracing e metriche.</summary>
public static class EmailDiagnostics
{
    /// <summary>Nome condiviso del Meter e della sorgente di tracing.</summary>
    public const string SourceName = "FDR11.Notifications.Email";
    internal static readonly ActivitySource Activities = new(SourceName);
    private static readonly Meter Meter = new(SourceName);
    private static readonly Counter<long> Completed = Meter.CreateCounter<long>("email.send.completed", "{send}", "Invii terminati per provider ed esito; non certifica la consegna.");
    private static readonly Histogram<double> Duration = Meter.CreateHistogram<double>("email.send.duration", "s", "Durata della chiamata di invio.");
    private static readonly UpDownCounter<long> Active = Meter.CreateUpDownCounter<long>("email.send.active", "{send}", "Chiamate di invio in corso.");

    internal static Task Send(string provider, ILogger logger, Func<Task> send)
    {
        SendAsync(provider, logger, send, CancellationToken.None).GetAwaiter().GetResult();
        return Task.CompletedTask;
    }

    internal static async Task SendAsync(string provider, ILogger logger, Func<Task> send, CancellationToken cancellationToken)
    {
        using var activity = Activities.StartActivity("email.send", ActivityKind.Client);
        activity?.SetTag("email.provider", provider);
        var operationId = Guid.NewGuid().ToString("N");
        var started = Stopwatch.GetTimestamp();
        var outcome = "success";
        string? errorType = null;
        var providerTag = new KeyValuePair<string, object?>("email.provider", provider);
        Active.Add(1, providerTag);
        logger.LogDebug(new EventId(1000, "EmailSendStarted"),
            "Email send started: {Provider} {OperationId} {TraceId}", provider, operationId, Activity.Current?.TraceId.ToString());
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await send().ConfigureAwait(false);
            activity?.SetStatus(ActivityStatusCode.Ok);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            outcome = "cancelled";
            throw;
        }
        catch (Exception exception)
        {
            outcome = "failure";
            errorType = exception.GetType().FullName;
            activity?.SetTag("error.type", errorType);
            // Exception messages and stack traces can contain recipient addresses or credentials.
            activity?.SetStatus(ActivityStatusCode.Error);
            throw;
        }
        finally
        {
            var elapsed = Stopwatch.GetElapsedTime(started).TotalSeconds;
            activity?.SetTag("email.outcome", outcome);
            var tags = new TagList { { "email.provider", provider }, { "email.outcome", outcome } };
            Active.Add(-1, providerTag);
            Completed.Add(1, tags);
            Duration.Record(elapsed, tags);
            logger.Log(outcome == "failure" ? LogLevel.Error : LogLevel.Information,
                new EventId(1001, "EmailSendCompleted"),
                "Email send completed: {Provider} {OperationId} {TraceId} {Outcome} {DurationSeconds} {ErrorType}",
                provider, operationId, Activity.Current?.TraceId.ToString(), outcome, elapsed, errorType);
        }
    }
}
