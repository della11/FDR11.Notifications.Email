using FDR11.Notifications.Email.Resilience;
using FDR11.Notifications.Email.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using FDR11.Notifications.Email.Abstracts;
using FDR11.Notifications.Email.Models;
using FDR11.Notifications.Email.Options;
using Microsoft.Extensions.Options;
using SendGrid;
using SendGrid.Helpers.Mail;


namespace FDR11.Notifications.Email.Concretes;


/// <summary>
/// Invia messaggi di posta elettronica tramite SendGrid.
/// </summary>
/// <param name="options">Impostazioni del provider di posta elettronica.</param>
public class SendGridEmailSender(IOptions<EmailOptions> options) : IEmailSender
{
    private readonly ILogger _logger = NullLogger<SendGridEmailSender>.Instance;

    /// <summary>Crea il sender con logging strutturato.</summary>
    public SendGridEmailSender(IOptions<EmailOptions> options, ILogger<SendGridEmailSender> logger) : this(options)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }


    /// <summary>
    /// Invia il messaggio di posta elettronica attendendo il completamento della chiamata al provider.
    /// </summary>
    /// <param name="message">Messaggio da inviare.</param>
    /// <returns>Attività che rappresenta l'operazione di invio.</returns>
    public Task Send(EmailMessage message) => EmailDiagnostics.Send("SendGrid", _logger, () => EmailRetry.ExecuteAsync(options.Value.Retry, "SendGrid", _logger, _ => SendCore(message), CancellationToken.None));

    private Task SendCore(EmailMessage message)
    {
        SendCoreAsync(message).GetAwaiter().GetResult();

        return Task.CompletedTask;
    }

    /// <summary>
    /// Invia il messaggio di posta elettronica in modo asincrono.
    /// </summary>
    /// <param name="message">Messaggio da inviare.</param>
    /// <param name="cancellationToken">Token per richiedere l'annullamento dell'invio.</param>
    /// <returns>Attività che rappresenta l'operazione di invio.</returns>
    public Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default) =>
        EmailDiagnostics.SendAsync("SendGrid", _logger, () => EmailRetry.ExecuteAsync(options.Value.Retry, "SendGrid", _logger, token => SendCoreAsync(message, token), cancellationToken), cancellationToken);

    private async Task SendCoreAsync(EmailMessage message, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var response = await CreateClient().SendEmailAsync(PrepareEmail(message), cancellationToken).ConfigureAwait(false);
        using var responseBody = response.Body;
        if ((int)response.StatusCode is < 200 or >= 300)
        {
            var error = new HttpRequestException($"SendGrid ha rifiutato l'invio (HTTP {(int)response.StatusCode}).", null, response.StatusCode);
            if (response.Headers?.TryGetValues("Retry-After", out var values) == true)
            {
                var headers = values.ToArray();
                if (headers.Length == 1) error.Data[EmailRetry.RetryAfterKey] = headers[0];
            }
            throw error;
        }
    }

    /// <summary>Crea il client del provider; sovrascrivibile per integrazioni e test.</summary>
    protected virtual ISendGridClient CreateClient() => new SendGridClient(options.Value.SendGridApiKey);

    private static SendGridMessage PrepareEmail(EmailMessage message)
    {
        var msg = new SendGridMessage
        {
            From = new EmailAddress(message.From),
            Subject = message.Subject
        };
        msg.AddTos(message.To.Select(x => new EmailAddress(x)).ToList());
        if (message.Bcc is { Count: > 0 })
            msg.AddBccs(message.Bcc.Select(x => new EmailAddress(x)).ToList());
        if (message.Cc is { Count: > 0 })
            msg.AddCcs(message.Cc.Select(x => new EmailAddress(x)).ToList());

        if (message.IsHtml)
            msg.HtmlContent = message.Body;
        else
            msg.PlainTextContent = message.Body;

        foreach (var attachment in message.Attachments ?? [])
            msg.AddAttachment(attachment.FileName, Convert.ToBase64String(attachment.Content), attachment.ContentType);
        return msg;
    }
}
