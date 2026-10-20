using System;
using System.Collections.Generic;
using System.Text;

namespace FDR11.Notifications.Email.Models;

/// <summary>
/// Rappresenta un allegato di posta elettronica con contenuto binario e tipo MIME.
/// </summary>
public class EmailAttachment
{
    /// <summary>
    /// Ottiene o imposta il nome del file allegato.
    /// </summary>
    public string FileName { get; set; }

    /// <summary>
    /// Ottiene o imposta il contenuto binario dell'allegato.
    /// </summary>
    public byte[] Content { get; set; }

    /// <summary>
    /// Ottiene o imposta il tipo MIME dell'allegato.
    /// </summary>
    public string ContentType { get; set; }
}
