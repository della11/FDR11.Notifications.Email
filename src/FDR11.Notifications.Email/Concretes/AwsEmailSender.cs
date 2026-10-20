using FDR11.Notifications.Email.Resilience;
using FDR11.Notifications.Email.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Amazon;
using Amazon.SimpleEmail;
using Amazon.SimpleEmail.Model;
using FDR11.Notifications.Email.Abstracts;
using FDR11.Notifications.Email.Models;
using FDR11.Notifications.Email.Options;
using Microsoft.Extensions.Options;

namespace FDR11.Notifications.Email.Concretes
{
    /// <summary>
    /// Invia messaggi di posta elettronica tramite Amazon Simple Email Service (SES).
    /// </summary>
    /// <param name="options">Impostazioni del provider di posta elettronica.</param>
    public class SesEmailSender(IOptions<EmailOptions> options) : IEmailSender
    {
        private readonly ILogger _logger = NullLogger<SesEmailSender>.Instance;

        /// <summary>Crea il sender con logging strutturato.</summary>
        public SesEmailSender(IOptions<EmailOptions> options, ILogger<SesEmailSender> logger) : this(options)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        }

        /// <summary>
        /// Invia il messaggio di posta elettronica attendendo il completamento della chiamata al provider.
        /// </summary>
        /// <param name="message">Messaggio da inviare.</param>
        /// <returns>Attività che rappresenta l'operazione di invio.</returns>
        public Task Send(EmailMessage message) => EmailDiagnostics.Send("SES", _logger, () => EmailRetry.ExecuteAsync(options.Value.Retry, "SES", _logger, _ => SendCore(message), CancellationToken.None));

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
            EmailDiagnostics.SendAsync("SES", _logger, () => EmailRetry.ExecuteAsync(options.Value.Retry, "SES", _logger, token => SendCoreAsync(message, token), cancellationToken), cancellationToken);

        private async Task SendCoreAsync(EmailMessage message, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var client = CreateClient();
            if (message.Attachments is { Count: > 0 })
            {
                using var data = new MemoryStream();
                using var mime = SmtpEmailSender.PrepareEmail(message);
                // Bcc belongs to the envelope, never to the raw MIME headers.
                mime.Bcc.Clear();
                await mime.WriteToAsync(data, cancellationToken).ConfigureAwait(false);
                data.Position = 0;
                await client.SendRawEmailAsync(new SendRawEmailRequest
                {
                    Source = message.From,
                    Destinations = message.To.Concat(message.Cc ?? []).Concat(message.Bcc ?? []).ToList(),
                    RawMessage = new RawMessage(data)
                }, cancellationToken).ConfigureAwait(false);
            }
            else
                await client.SendEmailAsync(PrepareEmail(message), cancellationToken).ConfigureAwait(false);
        }

        /// <summary>
        /// Crea il client SES con le credenziali e la regione configurate.
        /// </summary>
        /// <returns>Client da rilasciare al termine dell'invio.</returns>
        protected virtual IAmazonSimpleEmailService CreateClient() => new AmazonSimpleEmailServiceClient(
            options.Value.AwsAccessKey,
            options.Value.AwsSecretKey,
            CreateClientConfig());

        private AmazonSimpleEmailServiceConfig CreateClientConfig()
        {
            var config = new AmazonSimpleEmailServiceConfig { RegionEndpoint = RegionEndpoint.GetBySystemName(options.Value.AwsRegion) };
            if (options.Value.Retry.MaxRetryAttempts > 0) config.MaxErrorRetry = 0;
            return config;
        }

        /// <summary>
        /// Prepara il messaggio nel formato richiesto dal provider.
        /// </summary>
        /// <param name="message">Messaggio da convertire.</param>
        /// <returns>Richiesta di invio per Amazon SES.</returns>
        private static SendEmailRequest PrepareEmail(EmailMessage message)
        {
            return new SendEmailRequest
            {
                Source = message.From,
                Destination = new Destination
                {
                    ToAddresses = message.To,
                    CcAddresses = message.Cc,
                    BccAddresses = message.Bcc
                },
                Message = new Message
                {
                    Subject = new Content(message.Subject),
                    Body = new Body
                    {
                        Html = message.IsHtml ? new Content(message.Body) : null,
                        Text = !message.IsHtml ? new Content(message.Body) : null
                    }
                }
            };
        }
    }
}
