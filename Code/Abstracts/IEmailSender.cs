using FDR11.Notifications.Email.Models;

namespace FDR11.Notifications.Email.Abstracts;

/// <summary>
/// Definisce le operazioni per inviare messaggi di posta elettronica.
/// </summary>
public  interface IEmailSender
{
    /// <summary>
    /// Invia il messaggio di posta elettronica attendendo il completamento della chiamata al provider.
    /// </summary>
    /// <param name="message">Messaggio da inviare.</param>
    /// <returns>Attività che rappresenta l'operazione di invio.</returns>
    Task Send(EmailMessage message);
    /// <summary>
    /// Invia il messaggio di posta elettronica in modo asincrono.
    /// </summary>
    /// <param name="message">Messaggio da inviare.</param>
    /// <param name="cancellationToken">Token per richiedere l'annullamento dell'invio.</param>
    /// <returns>Attività che rappresenta l'operazione di invio.</returns>
    Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default);
}
