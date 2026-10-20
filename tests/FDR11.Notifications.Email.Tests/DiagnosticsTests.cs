using System.Diagnostics;
using System.Diagnostics.Metrics;
using FDR11.Notifications.Email.Abstracts;
using FDR11.Notifications.Email.Concretes;
using FDR11.Notifications.Email.Diagnostics;
using FDR11.Notifications.Email.Extensions.DI;
using FDR11.Notifications.Email.Models;
using FDR11.Notifications.Email.Options;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace FDR11.Notifications.Email.Tests;

[CollectionDefinition("Diagnostics", DisableParallelization = true)]
public class DiagnosticsCollection;

[Collection("Diagnostics")]
public class DiagnosticsTests
{
    [Theory]
    [InlineData("success", false)]
    [InlineData("failure", false)]
    [InlineData("cancelled", false)]
    [InlineData("success", true)]
    [InlineData("failure", true)]
    public async Task RecordsExactlyOneOutcomeWithCorrelationAndNoSensitiveData(string outcome, bool sync)
    {
        using var capture = new Capture();
        var logger = new RecordingLogger<SmtpEmailSender>();
        using var parent = new Activity("request").Start();
        using var cts = new CancellationTokenSource();
        var failure = new InvalidOperationException("SECRET recipient@example.test body password");
        var cancellation = new OperationCanceledException(cts.Token);
        async Task Send()
        {
            await Task.Yield();
            if (outcome == "failure") throw failure;
            if (outcome == "cancelled") { cts.Cancel(); throw cancellation; }
        }
        Task Invoke() => sync
            ? EmailDiagnostics.Send("SMTP", logger, Send)
            : EmailDiagnostics.SendAsync("SMTP", logger, Send, cts.Token);
        if (outcome == "failure") Assert.Same(failure, await Assert.ThrowsAsync<InvalidOperationException>(Invoke));
        else if (outcome == "cancelled") Assert.Same(cancellation, await Assert.ThrowsAsync<OperationCanceledException>(Invoke));
        else await Invoke();

        var span = Assert.Single(capture.Spans);
        Assert.Equal(parent.TraceId, span.TraceId);
        Assert.Equal(parent.SpanId, span.ParentSpanId);
        Assert.Equal(outcome, span.GetTagItem("email.outcome"));
        Assert.Equal("SMTP", span.GetTagItem("email.provider"));
        Assert.Equal(outcome == "failure" ? ActivityStatusCode.Error : outcome == "success" ? ActivityStatusCode.Ok : ActivityStatusCode.Unset, span.Status);
        Assert.Null(span.StatusDescription);
        Assert.Empty(span.Events);
        Assert.Same(parent, Activity.Current);

        var completed = Assert.Single(capture.Measurements, x => x.Name == "email.send.completed");
        Assert.Equal(1, completed.Value);
        Assert.Equal(outcome, completed.Tags["email.outcome"]);
        Assert.Equal("SMTP", completed.Tags["email.provider"]);
        Assert.Equal(2, completed.Tags.Count);
        var duration = Assert.Single(capture.Measurements, x => x.Name == "email.send.duration");
        Assert.True(duration.Value >= 0);
        Assert.Equal(new double[] { 1, -1 }, capture.Measurements.Where(x => x.Name == "email.send.active").Select(x => x.Value));
        Assert.All(capture.Measurements, x => Assert.DoesNotContain("OperationId", x.Tags.Keys));

        Assert.Equal(2, logger.Entries.Count);
        var start = logger.Entries[0]; var end = logger.Entries[1];
        Assert.Equal(1000, start.Id.Id); Assert.Equal(1001, end.Id.Id);
        Assert.Equal(start.Fields["OperationId"], end.Fields["OperationId"]);
        Assert.Equal(parent.TraceId.ToString(), end.Fields["TraceId"]);
        Assert.Equal(outcome, end.Fields["Outcome"]);
        Assert.Equal(outcome == "failure" ? LogLevel.Error : LogLevel.Information, end.Level);
        Assert.All(logger.Entries, x => { Assert.Null(x.Exception); Assert.DoesNotContain("SECRET", x.Text); });
        Assert.DoesNotContain("SECRET", string.Join(" ", span.TagObjects.Select(x => x.Value)));
    }

    [Fact]
    public async Task UnrequestedCancellationIsFailure()
    {
        using var capture = new Capture();
        var logger = new RecordingLogger<SmtpEmailSender>();
        await Assert.ThrowsAsync<OperationCanceledException>(() => EmailDiagnostics.SendAsync("SMTP", logger,
            () => throw new OperationCanceledException("provider timeout"), CancellationToken.None));
        Assert.Equal("failure", logger.Entries.Last().Fields["Outcome"]);
    }

    [Theory]
    [InlineData("SMTP")]
    [InlineData("SendGrid")]
    [InlineData("SES")]
    [InlineData("Azure")]
    public async Task EveryProviderIsInstrumentedWithoutLogger(string provider)
    {
        using var capture = new Capture();
        var options = Microsoft.Extensions.Options.Options.Create(new EmailOptions());
        IEmailSender sender = provider switch
        {
            "SMTP" => new SmtpEmailSender(options), "SendGrid" => new SendGridEmailSender(options),
            "SES" => new SesEmailSender(options), _ => new AzureEmailSender(options)
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sender.SendAsync(new EmailMessage(), new CancellationToken(true)));
        var span = Assert.Single(capture.Spans);
        Assert.Equal(provider, span.GetTagItem("email.provider"));
        Assert.Equal("cancelled", span.GetTagItem("email.outcome"));
        Assert.Single(capture.Measurements, x => x.Name == "email.send.completed");
    }

    [Fact]
    public async Task DependencyInjectionUsesApplicationLogger()
    {
        var logger = new RecordingLogger<SmtpEmailSender>();
        var services = new ServiceCollection();
        services.AddSingleton<ILogger<SmtpEmailSender>>(logger);
        services.AddEmailServices(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["EmailOptions:Provider"] = "SMTP", ["EmailOptions:SmtpHost"] = "host",
            ["EmailOptions:SmtpPort"] = "587", ["EmailOptions:Username"] = "user", ["EmailOptions:Password"] = "SECRET"
        }).Build());
        using var container = services.BuildServiceProvider();
        using var scope = container.CreateScope();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scope.ServiceProvider.GetRequiredService<IEmailSender>()
            .SendAsync(new EmailMessage { Body = "SECRET", From = "private@example.test" }, new CancellationToken(true)));
        Assert.Equal("cancelled", logger.Entries.Last().Fields["Outcome"]);
        Assert.All(logger.Entries, x => { Assert.DoesNotContain("SECRET", x.Text); Assert.DoesNotContain("private@example.test", x.Text); });
    }

    [Theory]
    [InlineData("SMTP")]
    [InlineData("SendGrid")]
    [InlineData("SES")]
    [InlineData("Azure")]
    public async Task SynchronousProviderFailuresAreRecordedOnce(string provider)
    {
        using var capture = new Capture();
        var options = Microsoft.Extensions.Options.Options.Create(new EmailOptions { SendGridApiKey = "test", AwsRegion = "eu-west-1" });
        IEmailSender sender = provider switch
        {
            "SMTP" => new SmtpEmailSender(options), "SendGrid" => new SendGridEmailSender(options),
            "SES" => new SesEmailSender(options), _ => new AzureEmailSender(options)
        };
        // Invalid input fails before network access.
        await Assert.ThrowsAnyAsync<Exception>(() => sender.Send(null!));
        Assert.Equal("failure", Assert.Single(capture.Spans).GetTagItem("email.outcome"));
        Assert.Single(capture.Measurements, x => x.Name == "email.send.completed");
    }

    [Fact]
    public async Task ConcurrentInvocationsBalanceActiveCounter()
    {
        using var capture = new Capture();
        var logger = Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance;
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = EmailDiagnostics.SendAsync("SMTP", logger, () => gate.Task, CancellationToken.None);
        var second = EmailDiagnostics.SendAsync("SMTP", logger, () => gate.Task, CancellationToken.None);
        Assert.Equal(2, capture.Measurements.Where(x => x.Name == "email.send.active").Sum(x => x.Value));
        gate.SetResult();
        await Task.WhenAll(first, second);
        Assert.Equal(0, capture.Measurements.Where(x => x.Name == "email.send.active").Sum(x => x.Value));
        Assert.Equal(2, capture.Measurements.Count(x => x.Name == "email.send.completed"));
    }

    private sealed record Measurement(string Name, double Value, Dictionary<string, object?> Tags);
    private sealed class Capture : IDisposable
    {
        public readonly System.Collections.Concurrent.ConcurrentQueue<Measurement> Measurements = new();
        public readonly System.Collections.Concurrent.ConcurrentQueue<Activity> Spans = new();
        private readonly MeterListener _meter = new();
        private readonly ActivityListener _activity;
        public Capture()
        {
            _activity = new ActivityListener
            {
                ShouldListenTo = source => source.Name == EmailDiagnostics.SourceName,
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllData,
                ActivityStopped = Spans.Enqueue
            };
            ActivitySource.AddActivityListener(_activity);
            _meter.InstrumentPublished = (instrument, listener) =>
            {
                if (instrument.Meter.Name == EmailDiagnostics.SourceName) listener.EnableMeasurementEvents(instrument);
            };
            _meter.SetMeasurementEventCallback<long>((instrument, value, tags, _) => Record(instrument, value, tags));
            _meter.SetMeasurementEventCallback<double>((instrument, value, tags, _) => Record(instrument, value, tags));
            _meter.Start();
        }
        private void Record(Instrument instrument, double value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
            => Measurements.Enqueue(new(instrument.Name, value, tags.ToArray().ToDictionary(x => x.Key, x => x.Value)));
        public void Dispose() { _meter.Dispose(); _activity.Dispose(); }
    }

    private sealed record Entry(LogLevel Level, EventId Id, string Text, Exception? Exception, Dictionary<string, object?> Fields);
    private sealed class RecordingLogger<T> : ILogger<T>
    {
        public readonly List<Entry> Entries = [];
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Add(new(logLevel, eventId, formatter(state, exception), exception,
                ((IEnumerable<KeyValuePair<string, object?>>)state!).ToDictionary(x => x.Key, x => x.Value)));
    }
}
