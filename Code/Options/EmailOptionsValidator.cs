using Microsoft.Extensions.Options;

namespace FDR11.Notifications.Email.Options;

/// <summary>Valida esclusivamente le impostazioni del provider selezionato.</summary>
public sealed class EmailOptionsValidator : IValidateOptions<EmailOptions>
{
    /// <inheritdoc />
    public ValidateOptionsResult Validate(string? name, EmailOptions options)
    {
        var errors = new List<string>();
        void Required(string value, string property)
        {
            if (string.IsNullOrWhiteSpace(value))
                errors.Add($"EmailOptions:{property} è obbligatorio.");
        }

        switch (options.Provider)
        {
            case "SMTP":
                Required(options.SmtpHost, nameof(options.SmtpHost));
                Required(options.Username, nameof(options.Username));
                Required(options.Password, nameof(options.Password));
                if (options.SmtpPort is < 1 or > 65535)
                    errors.Add("EmailOptions:SmtpPort deve essere compresa tra 1 e 65535.");
                break;
            case "SendGrid":
                Required(options.SendGridApiKey, nameof(options.SendGridApiKey));
                break;
            case "SES":
                Required(options.AwsAccessKey, nameof(options.AwsAccessKey));
                Required(options.AwsSecretKey, nameof(options.AwsSecretKey));
                Required(options.AwsRegion, nameof(options.AwsRegion));
                break;
            case "Azure":
            case "AZ":
                Required(options.AzureConnectionString, nameof(options.AzureConnectionString));
                break;
            default:
                errors.Add("EmailOptions:Provider deve essere SMTP, SendGrid, SES, Azure o AZ.");
                break;
        }

        try { Resilience.EmailRetry.Validate(options.Retry); }
        catch (OptionsValidationException exception) { errors.AddRange(exception.Failures); }

        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}
