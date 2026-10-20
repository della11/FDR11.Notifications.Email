using FDR11.Notifications.Email.Resilience;
using FDR11.Notifications.Email.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Azure;
using Azure.Communication.Email;
using FDR11.Notifications.Email.Abstracts;
using FDR11.Notifications.Email.Options;
using Microsoft.Extensions.Options;
using AzureEmailMessage = Azure.Communication.Email.EmailMessage;
using EmailMessage = FDR11.Notifications.Email.Models.EmailMessage;

namespace FDR11.Notifications.Email.Concretes;

/// <summary>
/// Invia messaggi di posta elettronica tramite Azure Communication Services.
/// </summary>
/// <param name="options">Impostazioni del provider di posta elettronica.</param>
public class AzureEmailSender(IOptions<EmailOptions> options) : IEmailSender
{
    private readonly ILogger _logger = NullLogger<AzureEmailSender>.Instance;

    /// <summary>Crea il sender con logging strutturato.</summary>
    public AzureEmailSender(IOptions<EmailOptions> options, ILogger<AzureEmailSender> logger) : this(options)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Invia il messaggio di posta elettronica attendendo il completamento della chiamata al provider.
    /// </summary>
    /// <param name="message">Messaggio da inviare.</param>
    /// <returns>Attività che rappresenta l'operazione di invio.</returns>
    public Task Send(EmailMessage message) => EmailDiagnostics.Send("Azure", _logger, () => SendCore(message));

    private Task SendCore(EmailMessage message)
    {
        EmailRetry.Validate(options.Value.Retry);
        if (options.Value.Retry.MaxRetryAttempts > 0)
        {
            SendCoreAsync(message).GetAwaiter().GetResult();
            return Task.CompletedTask;
        }
        var emailClient = CreateClient();

        _ = emailClient.Send(
             WaitUntil.Completed,
             PrepareMessage(message));

        return Task.CompletedTask;

    }

    /// <summary>
    /// Invia il messaggio di posta elettronica in modo asincrono.
    /// </summary>
    /// <param name="message">Messaggio da inviare.</param>
    /// <param name="cancellationToken">Token di annullamento inoltrato al client Azure.</param>
    /// <returns>Attività che rappresenta l'operazione di invio.</returns>
    public Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default) =>
        EmailDiagnostics.SendAsync("Azure", _logger, () => SendCoreAsync(message, cancellationToken), cancellationToken);

    private async Task SendCoreAsync(EmailMessage message, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var emailClient = CreateClient();

        EmailRetry.Validate(options.Value.Retry);
        if (options.Value.Retry.MaxRetryAttempts > 0)
        {
            var prepared = PrepareMessage(message);
            EmailSendOperation operation = null!;
            await EmailRetry.ExecuteAsync(options.Value.Retry, "Azure", _logger, async token =>
            {
                operation = await emailClient.SendAsync(WaitUntil.Started, prepared, token).ConfigureAwait(false);
            }, cancellationToken).ConfigureAwait(false);
            // Polling failure must never restart an already accepted send.
            await operation.WaitForCompletionAsync(cancellationToken).ConfigureAwait(false);
            return;
        }
        _ = await emailClient.SendAsync(
             WaitUntil.Completed,
             PrepareMessage(message), cancellationToken).ConfigureAwait(false);

    }

    /// <summary>Crea il client Azure; sovrascrivibile per integrazioni e test.</summary>
    protected virtual EmailClient CreateClient()
    {
        var clientOptions = new EmailClientOptions();
        if (options.Value.Retry.MaxRetryAttempts > 0) clientOptions.Retry.MaxRetries = 0;
        return new EmailClient(options.Value.AzureConnectionString, clientOptions);
    }

    private static AzureEmailMessage PrepareMessage(EmailMessage message)
    {
        EmailContent mailContent = new(message.Subject)
        {
            Html = message.IsHtml ? message.Body : null,
            PlainText = !message.IsHtml ? message.Body : null
        };

        var destinatari = new EmailRecipients(
            to: message.To.Select(x => new EmailAddress(x)),
            cc: message.Cc?.Select(x => new EmailAddress(x)),
            bcc: message.Bcc?.Select(x => new EmailAddress(x))
        );

        var emailMessage = new AzureEmailMessage(
            senderAddress: message.From,
            recipients: destinatari,
            content: mailContent);

        if (message.Attachments is not null && message.Attachments.Count > 0)
        {
            foreach (var att in message.Attachments)
            {
                var attachment = new EmailAttachment(att.FileName, att.ContentType, new BinaryData(att.Content));
                emailMessage.Attachments.Add(attachment);
            }
        }

        return emailMessage;
    }
}
