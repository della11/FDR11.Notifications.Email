using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using FDR11.Notifications.Email.Abstracts;
using FDR11.Notifications.Email.Concretes;
using FDR11.Notifications.Email.Options;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace FDR11.Notifications.Email.Extensions.DI;

/// <summary>
/// Fornisce metodi di estensione per registrare i servizi di posta elettronica.
/// </summary>
public static class EmailSenderExtensions
{
    extension(IServiceCollection services)
    {
        /// <summary>
        /// Configura le opzioni dalla sezione EmailOptions e registra il mittente con durata scoped.
        /// </summary>
        /// <param name="configuration">Configurazione contenente la sezione indicata da <see cref="EmailOptions.SectionName"/>.</param>
        /// <returns>La raccolta dei servizi per concatenare ulteriori registrazioni.</returns>
        /// <remarks>Le opzioni sono validate all’avvio dell’host e alla prima risoluzione.</remarks>
        public IServiceCollection AddEmailServices(IConfiguration configuration)
        {
            ArgumentNullException.ThrowIfNull(services);
            ArgumentNullException.ThrowIfNull(configuration);

            services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<EmailOptions>, EmailOptionsValidator>());
            services.AddOptions<EmailOptions>()
                .Configure(options => configuration.GetSection(EmailOptions.SectionName).Bind(options))
                .ValidateOnStart();

            _ = services.AddScoped<IEmailSender>(provider =>
            {
                var emailOptions = provider.GetRequiredService<IOptions<EmailOptions>>();
                return emailOptions.Value.Provider switch
                {
                    "SMTP" => new SmtpEmailSender(emailOptions, provider.GetService<ILogger<SmtpEmailSender>>() ?? NullLogger<SmtpEmailSender>.Instance),
                    "SendGrid" => new SendGridEmailSender(emailOptions, provider.GetService<ILogger<SendGridEmailSender>>() ?? NullLogger<SendGridEmailSender>.Instance),
                    "Azure" or "AZ" => new AzureEmailSender(emailOptions, provider.GetService<ILogger<AzureEmailSender>>() ?? NullLogger<AzureEmailSender>.Instance),
                    "SES" => new SesEmailSender(emailOptions, provider.GetService<ILogger<SesEmailSender>>() ?? NullLogger<SesEmailSender>.Instance),
                    _ => throw new NotSupportedException("Provider not supported")
                };
            });

            return services;
        }
    }
}
