namespace FDR11.Notifications.Email.Options;

/// <summary>Retry applicativi; zero mantiene il comportamento degli SDK esistente.</summary>
public sealed class EmailRetryOptions
{
    /// <summary>Numero massimo di retry aggiuntivi; zero disabilita la policy.</summary>
    public int MaxRetryAttempts { get; set; }
    /// <summary>Attesa iniziale del backoff esponenziale.</summary>
    public TimeSpan BaseDelay { get; set; } = TimeSpan.FromSeconds(1);
}
