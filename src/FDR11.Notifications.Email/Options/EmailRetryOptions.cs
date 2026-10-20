namespace FDR11.Notifications.Email.Options;

/// <summary>Retry applicativi; zero mantiene il comportamento degli SDK esistente.</summary>
public sealed class EmailRetryOptions
{
    public int MaxRetryAttempts { get; set; }
    public TimeSpan BaseDelay { get; set; } = TimeSpan.FromSeconds(1);
}
