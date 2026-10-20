using System.Reflection;
using Amazon.SimpleEmail.Model;
using FDR11.Notifications.Email.Abstracts;
using FDR11.Notifications.Email.Concretes;
using FDR11.Notifications.Email.Models;
using FDR11.Notifications.Email.Options;
using MimeKit;

namespace FDR11.Notifications.Email.Tests;

public class ProviderCompatibilityTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SesPreservesRecipientsAndBody(bool html)
    {
        var message = Message(html);
        var request = Prepare<SendEmailRequest>(typeof(SesEmailSender), message);
        Assert.Equal(message.From, request.Source);
        Assert.Equal(message.To, request.Destination.ToAddresses);
        Assert.Null(request.Destination.CcAddresses);
        Assert.Null(request.Destination.BccAddresses);
        Assert.Equal(message.Subject, request.Message.Subject.Data);
        Assert.Equal(message.Body, (html ? request.Message.Body.Html : request.Message.Body.Text).Data);
        Assert.Null(html ? request.Message.Body.Text : request.Message.Body.Html);
        message.Cc = ["cc@example.test"];
        message.Bcc = ["bcc@example.test"];
        request = Prepare<SendEmailRequest>(typeof(SesEmailSender), message);
        Assert.Equal(message.Cc, request.Destination.CcAddresses);
        Assert.Equal(message.Bcc, request.Destination.BccAddresses);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void SmtpPreservesBodyAndAttachmentAcrossMimeSerialization(bool html)
    {
        var message = Message(html);
        byte[] content = [0, 1, 2, 255];
        message.Attachments = [new() { FileName = "data.bin", ContentType = "application/octet-stream", Content = content }];
        using var mime = Prepare<MimeMessage>(typeof(SmtpEmailSender), message);
        using var stream = new MemoryStream();
        mime.WriteTo(stream, TestContext.Current.CancellationToken);
        stream.Position = 0;
        using var decoded = MimeMessage.Load(stream, TestContext.Current.CancellationToken);
        Assert.Equal(message.Body, html ? decoded.HtmlBody : decoded.TextBody);
        Assert.Equal(message.From, decoded.From.Mailboxes.Single().Address);
        Assert.Equal(message.To.Single(), decoded.To.Mailboxes.Single().Address);
        var attachment = Assert.IsType<MimePart>(Assert.Single(decoded.Attachments));
        Assert.Equal("data.bin", attachment.FileName);
        Assert.Equal("application/octet-stream", attachment.ContentType.MimeType);
        using var bytes = new MemoryStream();
        Assert.NotNull(attachment.Content);
        attachment.Content.DecodeTo(bytes, TestContext.Current.CancellationToken);
        Assert.Equal(content, bytes.ToArray());
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task CancelledSendDoesNotConnect(bool ses)
    {
        var options = Microsoft.Extensions.Options.Options.Create(new EmailOptions());
        IEmailSender sender = ses ? new SesEmailSender(options) : new SmtpEmailSender(options);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => sender.SendAsync(Message(false), new CancellationToken(true)));
    }

    private static EmailMessage Message(bool html) => new()
    {
        From = "sender@example.test", To = ["recipient@example.test"],
        Subject = "Test è", Body = html ? "<p>Caffè</p>" : "Caffè", IsHtml = html
    };

    private static T Prepare<T>(Type type, EmailMessage message) =>
        (T)type.GetMethod("PrepareEmail", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [message])!;
}
