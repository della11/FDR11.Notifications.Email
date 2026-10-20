namespace FDR11.Notifications.Email.Options;

/// <summary>
/// Contiene le impostazioni di connessione e autenticazione dei provider di posta elettronica.
/// </summary>
public  class EmailOptions
{
    public EmailRetryOptions Retry { get; set; } = new();
    /// <summary>
    /// Nome della sezione di configurazione delle opzioni di posta elettronica.
    /// </summary>
    public static string SectionName = "EmailOptions";
    /// <summary>
    /// Ottiene o imposta il provider da registrare: SMTP, SendGrid, SES o Azure (alias AZ).
    /// </summary>
    public string Provider { get; set; } = string.Empty;    

    /// <summary>
    /// Ottiene o imposta il nome host del server SMTP.
    /// </summary>
    public string SmtpHost { get; set; } = string.Empty;
    /// <summary>
    /// Ottiene o imposta la porta del server SMTP.
    /// </summary>
    public int SmtpPort { get; set; }

    /// <summary>
    /// Ottiene o imposta il nome utente per l'autenticazione SMTP.
    /// </summary>
    public string Username {  get; set; } = string.Empty;

    /// <summary>
    /// Ottiene o imposta la password per l'autenticazione SMTP.
    /// </summary>
    public string Password { get; set; } = string.Empty;

    /// <summary>
    /// Ottiene o imposta la chiave API di SendGrid.
    /// </summary>
    public string SendGridApiKey { get; set; } = string.Empty;

    /// <summary>
    /// Ottiene o imposta la chiave di accesso AWS per Amazon SES.
    /// </summary>
    public string AwsAccessKey { get; set; } = string.Empty;

    /// <summary>
    /// Ottiene o imposta la chiave segreta AWS per Amazon SES.
    /// </summary>
    public string AwsSecretKey { get; set; } = string.Empty;

    /// <summary>
    /// Ottiene o imposta il nome di sistema della regione AWS per Amazon SES.
    /// </summary>
    public string AwsRegion { get; set; } = string.Empty;
    /// <summary>
    /// Ottiene o imposta la stringa di connessione di Azure Communication Services.
    /// </summary>
    public string AzureConnectionString { get; set; } = string.Empty;
}
