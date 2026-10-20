using FDR11.Notifications.Email.Resilience;
using FDR11.Notifications.Email.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using FDR11.Notifications.Email.Abstracts;
using FDR11.Notifications.Email.Models;
using FDR11.Notifications.Email.Options;
using Microsoft.Extensions.Options;
using MailKit.Net.Smtp;
using MimeKit;

namespace FDR11.Notifications.Email.Concretes;

/// <summary>
/// Invia messaggi di posta elettronica tramite SMTP con connessione STARTTLS.
/// </summary>
/// <param name="emailOptions">Impostazioni del provider di posta elettronica.</param>
public class SmtpEmailSender(IOptions<EmailOptions> emailOptions) : IEmailSender
{
    private readonly ILogger _logger = NullLogger<SmtpEmailSender>.Instance;

    /// <summary>Crea il sender con logging strutturato.</summary>
    public SmtpEmailSender(IOptions<EmailOptions> emailOptions, ILogger<SmtpEmailSender> logger) : this(emailOptions)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Invia il messaggio di posta elettronica attendendo il completamento della chiamata al provider.
    /// </summary>
    /// <param name="message">Messaggio da inviare.</param>
    /// <returns>Attività che rappresenta l'operazione di invio.</returns>
    public Task Send(EmailMessage message) => EmailDiagnostics.Send("SMTP", _logger, () => EmailRetry.ExecuteAsync(emailOptions.Value.Retry, "SMTP", _logger, _ => SendCore(message), CancellationToken.None));

    private Task SendCore(EmailMessage message)
    {
        using MimeMessage email = PrepareEmail(message);
        using var smtp = new SmtpClient();
        try
        {
            smtp.Connect(emailOptions.Value.SmtpHost, emailOptions.Value.SmtpPort, MailKit.Security.SecureSocketOptions.StartTls, default);

            smtp.Authenticate(emailOptions.Value.Username, emailOptions.Value.Password);

            _ = smtp.Send(email);
            smtp.Disconnect(true);
        }
        finally
        {
            if (smtp.IsConnected)
                smtp.Disconnect(false);
        }
        return Task.CompletedTask;
    }


    /// <summary>
    /// Invia il messaggio di posta elettronica in modo asincrono.
    /// </summary>
    /// <param name="message">Messaggio da inviare.</param>
    /// <param name="cancellationToken">Token per richiedere l'annullamento dell'invio.</param>
    /// <returns>Attività che rappresenta l'operazione di invio.</returns>
    public Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default) =>
        EmailDiagnostics.SendAsync("SMTP", _logger, () => EmailRetry.ExecuteAsync(emailOptions.Value.Retry, "SMTP", _logger, token => SendCoreAsync(message, token), cancellationToken), cancellationToken);

    private async Task SendCoreAsync(EmailMessage message, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using MimeMessage email = PrepareEmail(message);
        using var smtp = new SmtpClient();
        try
        {


            await smtp.ConnectAsync(emailOptions.Value.SmtpHost, emailOptions.Value.SmtpPort, MailKit.Security.SecureSocketOptions.StartTls, cancellationToken).ConfigureAwait(false);

            await smtp.AuthenticateAsync(emailOptions.Value.Username, emailOptions.Value.Password, cancellationToken).ConfigureAwait(false);

            await smtp.SendAsync(email, cancellationToken).ConfigureAwait(false);
            await smtp.DisconnectAsync(true, cancellationToken).ConfigureAwait(false);
        }

        finally
        {
            if (smtp.IsConnected)
                await smtp.DisconnectAsync(false, CancellationToken.None).ConfigureAwait(false);
        }
    }


    /// <summary>
    /// Prepara il messaggio nel formato richiesto dal provider.
    /// </summary>
    /// <param name="message">Messaggio da convertire.</param>
    /// <returns>Messaggio convertito nel formato del provider.</returns>
    internal static MimeMessage PrepareEmail(EmailMessage message)
    {
        var email = new MimeMessage();

        email.From.Add(MailboxAddress.Parse(message.From));

        foreach (var to in message.To)
            email.To.Add(MailboxAddress.Parse(to));

       if(message.Cc is not null)
        foreach (var cc in message.Cc)
            email.Cc.Add(MailboxAddress.Parse(cc));

       if(message.Bcc is not null)
            foreach (var bcc in message.Bcc)
            email.Bcc.Add(MailboxAddress.Parse(bcc));

        email.Subject = message.Subject;

        var bodyBuilder = new BodyBuilder();

        if (message.IsHtml)
            bodyBuilder.HtmlBody = message.Body;
        else
            bodyBuilder.TextBody = message.Body;


        if (message.Attachments is not null)
        {
            foreach (var attachment in message.Attachments)
            {
                bodyBuilder.Attachments.Add(
                    attachment.FileName,
                    attachment.Content,
                    ContentType.Parse(attachment.ContentType));
            }
        }

        email.Body = bodyBuilder.ToMessageBody();
        return email;
    }
}
