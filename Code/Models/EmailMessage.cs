namespace FDR11.Notifications.Email.Models;

/// <summary>
/// Rappresenta un messaggio di posta elettronica con destinatari, contenuto e allegati.
/// </summary>
public class EmailMessage
{
    /// <summary>
    /// Ottiene o imposta l'indirizzo di posta elettronica del mittente.
    /// </summary>
    public string From { get; set; }

    /// <summary>
    /// Ottiene o imposta la lista dei destinatari principali, inizialmente vuota.
    /// </summary>
    public List<string> To { get; set; } = [];

    /// <summary>
    /// Ottiene o imposta i destinatari in copia conoscenza, se presenti.
    /// </summary>
    public List<string>? Cc { get; set; }

    /// <summary>
    /// Ottiene o imposta i destinatari in copia conoscenza nascosta, se presenti.
    /// </summary>
    public List<string>? Bcc { get; set; }

    /// <summary>
    /// Ottiene o imposta l'oggetto del messaggio.
    /// </summary>
    public string Subject { get; set; }

    /// <summary>
    /// Ottiene o imposta il corpo del messaggio.
    /// </summary>
    public string Body { get; set; }

    /// <summary>
    /// Ottiene o imposta un valore che indica se il corpo è in formato HTML. Il valore predefinito è true.
    /// </summary>
    public bool IsHtml { get; set; } = true;

    /// <summary>
    /// Ottiene o imposta gli allegati del messaggio, se presenti. Supportati da tutti i provider, nei limiti del servizio remoto.
    /// </summary>
    public List<EmailAttachment>? Attachments { get; set; }
}
