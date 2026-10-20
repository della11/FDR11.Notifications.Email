using System.Net;
using System.Text.Json;
using Amazon;
using Amazon.SimpleEmail;
using Amazon.SimpleEmail.Model;
using Azure;
using Azure.Communication.Email;
using FDR11.Notifications.Email.Abstracts;
using FDR11.Notifications.Email.Concretes;
using FDR11.Notifications.Email.Extensions.DI;
using FDR11.Notifications.Email.Options;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using MimeKit;
using SendGrid;
using EmailMessage = FDR11.Notifications.Email.Models.EmailMessage;

namespace FDR11.Notifications.Email.Tests;

public class ConsolidationTests
{
    private static EmailMessage Message(bool html = false) => new()
    {
        From = "sender@example.test", To = ["to@example.test"], Subject = "Caffè",
        Body = html ? "<p>Caffè</p>" : "Caffè", IsHtml = html,
        Attachments = [new() { FileName = "data.bin", ContentType = "application/octet-stream", Content = [42] }]
    };

    private static IOptions<EmailOptions> Options() => Microsoft.Extensions.Options.Options.Create(new EmailOptions());

    [Theory]
    [InlineData("SMTP", typeof(SmtpEmailSender))]
    [InlineData("SendGrid", typeof(SendGridEmailSender))]
    [InlineData("SES", typeof(SesEmailSender))]
    [InlineData("Azure", typeof(AzureEmailSender))]
    [InlineData("AZ", typeof(AzureEmailSender))]
    public void ConfigurationSelectsProvider(string provider, Type expected)
    {
        using var services = Services(provider, true);
        using var scope = services.CreateScope();
        Assert.IsType(expected, scope.ServiceProvider.GetRequiredService<IEmailSender>());
        Assert.Same(scope.ServiceProvider.GetRequiredService<IEmailSender>(), scope.ServiceProvider.GetRequiredService<IEmailSender>());
    }

    [Theory]
    [InlineData("SMTP")]
    [InlineData("SendGrid")]
    [InlineData("SES")]
    [InlineData("Azure")]
    [InlineData("AZ")]
    [InlineData("invalid")]
    [InlineData("")]
    public void InvalidOptionsFailAtStartupAndResolution(string provider)
    {
        using var services = Services(provider, false);
        Assert.Throws<OptionsValidationException>(() => services.GetRequiredService<IStartupValidator>().Validate());
        Assert.Throws<OptionsValidationException>(() => services.GetRequiredService<IEmailSender>());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(65536)]
    public void SmtpRejectsInvalidPort(int port)
    {
        var result = new EmailOptionsValidator().Validate(null, new()
        {
            Provider = "SMTP", SmtpHost = "host", Username = "user", Password = "secret", SmtpPort = port
        });
        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, x => x.Contains("SmtpPort"));
        Assert.DoesNotContain("secret", result.FailureMessage);
    }

    [Theory]
    [InlineData("SMTP", "SmtpHost")]
    [InlineData("SMTP", "Username")]
    [InlineData("SMTP", "Password")]
    [InlineData("SendGrid", "SendGridApiKey")]
    [InlineData("SES", "AwsAccessKey")]
    [InlineData("SES", "AwsSecretKey")]
    [InlineData("SES", "AwsRegion")]
    [InlineData("Azure", "AzureConnectionString")]
    public void WhitespaceRequiredFieldIsRejected(string provider, string field)
    {
        var options = new EmailOptions
        {
            Provider = provider, SmtpHost = "host", SmtpPort = 587, Username = "user", Password = "secret",
            SendGridApiKey = "key", AwsAccessKey = "key", AwsSecretKey = "secret", AwsRegion = "eu-west-1",
            AzureConnectionString = "configured"
        };
        typeof(EmailOptions).GetProperty(field)!.SetValue(options, "  ");
        var result = new EmailOptionsValidator().Validate(null, options);
        Assert.True(result.Failed);
        Assert.Contains(field, Assert.Single(result.Failures!));
    }

    private static ServiceProvider Services(string provider, bool valid)
    {
        var values = new Dictionary<string, string?> { ["EmailOptions:Provider"] = provider };
        if (valid)
        {
            // Only configure the selected provider, so unrelated credentials cannot be required.
            var fields = provider switch
            {
                "SMTP" => new[] { "SmtpHost", "Username", "Password" },
                "SendGrid" => ["SendGridApiKey"],
                "SES" => ["AwsAccessKey", "AwsSecretKey", "AwsRegion"],
                _ => ["AzureConnectionString"]
            };
            foreach (var field in fields) values[$"EmailOptions:{field}"] = "configured";
            values["EmailOptions:SmtpPort"] = "587";
        }
        var services = new ServiceCollection();
        services.AddEmailServices(new ConfigurationBuilder().AddInMemoryCollection(values).Build());
        return services.BuildServiceProvider();
    }

    [Theory]
    [InlineData("SMTP")]
    [InlineData("SendGrid")]
    [InlineData("SES")]
    [InlineData("Azure")]
    public async Task PreCancelledSendDoesNotCreateClient(string provider)
    {
        IEmailSender sender = provider switch
        {
            "SMTP" => new SmtpEmailSender(Options()),
            "SendGrid" => new SendGridEmailSender(Options()),
            "SES" => new SesEmailSender(Options()),
            _ => new AzureEmailSender(Options())
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sender.SendAsync(Message(), new CancellationToken(true)));
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(false, false)]
    public async Task SendGridPreservesPayloadAndToken(bool html, bool copies)
    {
        using var handler = new RecordingHandler(HttpStatusCode.Accepted);
        using var http = new HttpClient(handler);
        var sender = new TestSendGrid(new SendGridClient(http, "test-key"));
        var message = Message(html);
        if (copies) { message.Cc = ["cc@example.test"]; message.Bcc = ["bcc@example.test"]; }
        using var cts = new CancellationTokenSource();
        await sender.SendAsync(message, cts.Token);
        using var payload = JsonDocument.Parse(handler.Payload!);
        var root = payload.RootElement;
        Assert.Equal(message.From, root.GetProperty("from").GetProperty("email").GetString());
        Assert.Equal(message.Subject, root.GetProperty("subject").GetString());
        var content = root.GetProperty("content")[0];
        Assert.Equal(html ? "text/html" : "text/plain", content.GetProperty("type").GetString());
        Assert.Equal(message.Body, content.GetProperty("value").GetString());
        var recipient = root.GetProperty("personalizations")[0];
        Assert.Equal("to@example.test", recipient.GetProperty("to")[0].GetProperty("email").GetString());
        Assert.Equal(copies, recipient.TryGetProperty("cc", out _));
        Assert.Equal(copies, recipient.TryGetProperty("bcc", out _));
        var attachment = root.GetProperty("attachments")[0];
        Assert.Equal("data.bin", attachment.GetProperty("filename").GetString());
        Assert.Equal("application/octet-stream", attachment.GetProperty("type").GetString());
        Assert.Equal(new byte[] { 42 }, Convert.FromBase64String(attachment.GetProperty("content").GetString()!));
        Assert.True(handler.Token.CanBeCanceled);
    }

    [Theory]
    [InlineData(400, false)]
    [InlineData(401, true)]
    [InlineData(429, false)]
    [InlineData(500, true)]
    public async Task SendGridRejectsUnsuccessfulResponses(int status, bool sync)
    {
        using var handler = new RecordingHandler((HttpStatusCode)status);
        using var http = new HttpClient(handler);
        var sender = new TestSendGrid(new SendGridClient(http, "test-key"));
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => sync ? sender.Send(Message()) : sender.SendAsync(Message(), TestContext.Current.CancellationToken));
        Assert.Equal((HttpStatusCode)status, error.StatusCode);
        Assert.DoesNotContain("private response", error.Message);
    }

    [Fact]
    public async Task SendGridPropagatesCancellationDuringRequest()
    {
        using var cts = new CancellationTokenSource();
        using var handler = new RecordingHandler(HttpStatusCode.Accepted) { OnSend = cts.Cancel };
        using var http = new HttpClient(handler);
        var sender = new TestSendGrid(new SendGridClient(http, "test-key"));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sender.SendAsync(Message(), cts.Token));
    }

    [Fact]
    public async Task AzurePropagatesCancellationDuringRequest()
    {
        using var cts = new CancellationTokenSource();
        var client = new RecordingAzure { OnSend = cts.Cancel };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new TestAzure(client).SendAsync(Message(), cts.Token));
    }

    [Fact]
    public async Task AzureAllowsAbsentOptionalCollections()
    {
        var client = new RecordingAzure();
        var message = Message();
        message.Attachments = null;
        await new TestAzure(client).SendAsync(message, TestContext.Current.CancellationToken);
        Assert.Empty(client.Message!.Recipients.CC);
        Assert.Empty(client.Message.Recipients.BCC);
        Assert.Empty(client.Message.Attachments);
    }

    [Fact]
    public async Task SesWithoutAttachmentsUsesStructuredRequest()
    {
        using var client = new RecordingSes();
        var message = Message();
        message.Attachments = null;
        await new TestSes(client).SendAsync(message, TestContext.Current.CancellationToken);
        Assert.Null(client.Data);
        Assert.Equal(message.Body, client.Structured!.Message.Body.Text.Data);
        Assert.Equal(TestContext.Current.CancellationToken, client.Token);
    }

    [Fact]
    public async Task SesPropagatesCancellationDuringRequest()
    {
        using var cts = new CancellationTokenSource();
        using var client = new RecordingSes { OnSend = cts.Cancel };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new TestSes(client).SendAsync(Message(), cts.Token));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task AzurePreservesBodyAttachmentRecipientsAndToken(bool html, bool sync)
    {
        var client = new RecordingAzure();
        var sender = new TestAzure(client);
        var message = Message(html);
        message.Cc = ["cc@example.test"]; message.Bcc = ["bcc@example.test"];
        using var cts = new CancellationTokenSource();
        if (sync) await sender.Send(message); else await sender.SendAsync(message, cts.Token);
        var sent = client.Message!;
        Assert.Equal(message.From, sent.SenderAddress);
        Assert.Equal(message.Subject, sent.Content.Subject);
        Assert.Equal(html ? message.Body : null, sent.Content.Html);
        Assert.Equal(html ? null : message.Body, sent.Content.PlainText);
        Assert.Equal("to@example.test", Assert.Single(sent.Recipients.To).Address);
        Assert.Equal("cc@example.test", Assert.Single(sent.Recipients.CC).Address);
        Assert.Equal("bcc@example.test", Assert.Single(sent.Recipients.BCC).Address);
        var attachment = Assert.Single(sent.Attachments);
        Assert.Equal("data.bin", attachment.Name);
        Assert.Equal("application/octet-stream", attachment.ContentType);
        Assert.Equal(new byte[] { 42 }, attachment.Content.ToArray());
        Assert.Equal(sync ? CancellationToken.None : cts.Token, client.Token);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task SesAttachmentsUseMimeWithoutLeakingBcc(bool html, bool sync)
    {
        using var client = new RecordingSes();
        var sender = new TestSes(client);
        var message = Message(html);
        message.Cc = ["cc@example.test"]; message.Bcc = ["bcc@example.test"];
        using var cts = new CancellationTokenSource();
        if (sync) await sender.Send(message); else await sender.SendAsync(message, cts.Token);
        Assert.Equal(message.From, client.Source);
        Assert.Equal(new[] { "to@example.test", "cc@example.test", "bcc@example.test" }, client.Destinations);
        using var mime = MimeMessage.Load(new MemoryStream(client.Data!), TestContext.Current.CancellationToken);
        Assert.Empty(mime.Bcc);
        Assert.Equal(message.Body, html ? mime.HtmlBody : mime.TextBody);
        Assert.Equal(message.Subject, mime.Subject);
        var attachment = Assert.IsType<MimePart>(Assert.Single(mime.Attachments));
        Assert.Equal("data.bin", attachment.FileName);
        Assert.Equal("application/octet-stream", attachment.ContentType.MimeType);
        using var bytes = new MemoryStream();
        attachment.Content!.DecodeTo(bytes, TestContext.Current.CancellationToken);
        Assert.Equal(new byte[] { 42 }, bytes.ToArray());
        Assert.Equal(sync ? CancellationToken.None : cts.Token, client.Token);
    }

    private sealed class RecordingHandler(HttpStatusCode status) : HttpMessageHandler
    {
        public string? Payload;
        public CancellationToken Token;
        public Action? OnSend;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Token = cancellationToken;
            OnSend?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();
            Payload = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(status) { Content = new StringContent("private response") };
        }
    }

    private sealed class TestSendGrid(ISendGridClient client) : SendGridEmailSender(Options())
    {
        protected override ISendGridClient CreateClient() => client;
    }

    private sealed class TestAzure(EmailClient client) : AzureEmailSender(Options())
    {
        protected override EmailClient CreateClient() => client;
    }

    private sealed class RecordingAzure : EmailClient
    {
        public Azure.Communication.Email.EmailMessage? Message;
        public CancellationToken Token;
        public Action? OnSend;
        public override EmailSendOperation Send(WaitUntil waitUntil, Azure.Communication.Email.EmailMessage message, CancellationToken cancellationToken = default)
        {
            Assert.Equal(WaitUntil.Completed, waitUntil);
            Message = message; Token = cancellationToken;
            OnSend?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();
            return new TestOperation();
        }
        public override Task<EmailSendOperation> SendAsync(WaitUntil waitUntil, Azure.Communication.Email.EmailMessage message, CancellationToken cancellationToken = default)
            => Task.FromResult(Send(waitUntil, message, cancellationToken));
    }

    private sealed class TestOperation : EmailSendOperation { }

    private sealed class TestSes(IAmazonSimpleEmailService client) : SesEmailSender(Options())
    {
        protected override IAmazonSimpleEmailService CreateClient() => client;
    }

    private sealed class RecordingSes() : AmazonSimpleEmailServiceClient("key", "secret", RegionEndpoint.EUWest1)
    {
        public byte[]? Data;
        public string? Source;
        public List<string>? Destinations;
        public CancellationToken Token;
        public Action? OnSend;
        public SendEmailRequest? Structured;
        public override Task<SendEmailResponse> SendEmailAsync(SendEmailRequest request, CancellationToken cancellationToken = default)
        {
            Structured = request; Token = cancellationToken;
            OnSend?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new SendEmailResponse());
        }
        public override Task<SendRawEmailResponse> SendRawEmailAsync(SendRawEmailRequest request, CancellationToken cancellationToken = default)
        {
            Assert.Equal(0, request.RawMessage.Data.Position);
            Data = request.RawMessage.Data.ToArray(); Source = request.Source;
            Destinations = request.Destinations; Token = cancellationToken;
            OnSend?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new SendRawEmailResponse());
        }
    }
}
