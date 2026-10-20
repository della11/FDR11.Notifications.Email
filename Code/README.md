# FDR11.Notifications.Email

![FDR11.Notifications.Email — invio email multiprovider](https://raw.githubusercontent.com/della11/FDR11.Notifications.Email/main/Code/assets/fdr11-notifications-email-icon.png)

Libreria C# per l'invio di email tramite SMTP (MailKit), SendGrid, Amazon SES e Azure Communication Services. Espone il contratto `IEmailSender`, modelli condivisi e un'estensione per la Dependency Injection.

Il progetto usa .NET 10 (`net10.0`). Le astrazioni e le implementazioni dei provider sono contenute nello stesso assembly.

## Installazione

Se il pacchetto è disponibile nel feed NuGet configurato:

```shell
dotnet add package FDR11.Notifications.Email
```

In alternativa, aggiungere un riferimento al progetto `Code/FDR11.Notifications.Email.csproj`. La presenza del progetto non implica che il pacchetto sia pubblicato su nuget.org.

## Configurazione e registrazione

`AddEmailServices` legge la sezione **EmailOptions** e registra `IEmailSender` con durata **scoped**. I valori di `Provider` riconosciuti sono `SMTP`, `SendGrid`, `SES`, `Azure` e l'alias `AZ`, con questa esatta capitalizzazione.

Le opzioni vengono validate all'avvio dell'host tramite `ValidateOnStart` e alla prima risoluzione. Senza un host, la validazione avviene alla risoluzione delle opzioni. Un provider sconosciuto, campi obbligatori vuoti o una porta SMTP fuori dall'intervallo 1–65535 generano `OptionsValidationException`. Si validano solo i campi del provider selezionato; la validità delle credenziali sul servizio remoto è verificata durante l'invio. La costruzione manuale dei sender con `Options.Create` non attiva questa validazione.

Esempio di `appsettings.json` per SMTP:

```json
{
  "EmailOptions": {
    "Provider": "SMTP",
    "SmtpHost": "smtp.example.com",
    "SmtpPort": 587,
    "Username": "smtp-user"
  }
}
```

Fornire `EmailOptions:Password` tramite la configurazione dell'applicazione, ad esempio con secret locali o la variabile d'ambiente `EmailOptions__Password`. Non inserire credenziali reali negli esempi versionati.

```csharp
using FDR11.Notifications.Email.Extensions.DI;

builder.Services.AddEmailServices(builder.Configuration);
```

| Provider | Opzioni utilizzate | Registrazione automatica |
| --- | --- | --- |
| SMTP | `SmtpHost`, `SmtpPort`, `Username`, `Password` | `Provider = "SMTP"` |
| SendGrid | `SendGridApiKey` | `Provider = "SendGrid"` |
| Amazon SES | `AwsAccessKey`, `AwsSecretKey`, `AwsRegion` | `Provider = "SES"` |
| Azure Communication Services | `AzureConnectionString` | `Provider = "Azure"` oppure `"AZ"` |

Per Azure, impostare `EmailOptions:Provider` a `Azure` (oppure `AZ`), fornire `EmailOptions:AzureConnectionString` tramite la configurazione dei secret e usare lo stesso `AddEmailServices`.

## Utilizzo

Ricevere `IEmailSender` tramite Dependency Injection:

```csharp
using FDR11.Notifications.Email.Abstracts;
using FDR11.Notifications.Email.Models;

public sealed class WelcomeService(IEmailSender emailSender)
{
    public Task SendWelcomeAsync(
        string recipient,
        CancellationToken cancellationToken = default)
    {
        return emailSender.SendAsync(new EmailMessage
        {
            From = "noreply@example.com",
            To = [recipient],
            Subject = "Benvenuto",
            Body = "<h1>Benvenuto!</h1>",
            IsHtml = true
        }, cancellationToken);
    }
}
```

`EmailMessage` contiene `From`, `To`, `Cc`, `Bcc`, `Subject`, `Body`, `IsHtml` e `Attachments`. `To` è inizialmente una lista vuota; `Cc`, `Bcc` e `Attachments` sono opzionali. `IsHtml` vale `true` per impostazione predefinita. Impostarlo a `false` per un corpo testuale.

Un allegato è rappresentato da `EmailAttachment`:

```csharp
var attachment = new EmailAttachment
{
    FileName = "report.pdf",
    ContentType = "application/pdf",
    Content = pdfBytes // byte[] fornito dall'applicazione
};
```

Assegnarlo a `EmailMessage.Attachments`. Tutti e quattro i provider trasferiscono nome, tipo MIME e contenuto; restano applicabili i limiti del servizio remoto.

### Contratto di invio

```csharp
Task Send(EmailMessage message);
Task SendAsync(EmailMessage message, CancellationToken cancellationToken = default);
```

Preferire `SendAsync`. Le implementazioni di `Send` eseguono una chiamata bloccante e restituiscono `Task.CompletedTask` al termine; non accettano un token di cancellazione. Nessuno dei due metodi restituisce un identificativo del messaggio o un risultato strutturato. Il completamento della chiamata non certifica la consegna nella casella del destinatario.

## Comportamento e limiti attuali

| Funzionalità | SMTP | SendGrid | Amazon SES | Azure |
| --- | --- | --- | --- | --- |
| Destinatari To/Cc/Bcc | Sì | Sì | Sì | Sì |
| Corpo HTML/testo | Sì | Sì | Sì | Sì |
| Allegati | MIME | Base64 | MIME (raw email) | BinaryData |
| Token in `SendAsync` | Inoltrato | Inoltrato | Inoltrato | Inoltrato |

Dettagli dell'implementazione:

- **SMTP:** usa STARTTLS e tenta sempre l'autenticazione con username e password configurati.
- **SendGrid:** Cc/Bcc null o vuoti vengono omessi. Gli allegati sono codificati in Base64. Le risposte non 2xx generano `HttpRequestException` con `StatusCode`, sia in `Send` sia in `SendAsync`; il corpo della risposta non viene incluso nell'eccezione.
- **Amazon SES:** usa `SendEmailRequest` senza allegati e `SendRawEmailRequest` con allegati MIME. I destinatari Bcc sono nell'envelope ma non negli header del messaggio raw.
- **Azure:** preserva il corpo HTML o testuale e nome, tipo MIME e contenuto degli allegati, inclusi quelli di un byte. Il token viene inoltrato al client.
- **Cancellazione:** tutti i provider interrompono un invio già annullato prima di creare il client e inoltrano il token durante le operazioni asincrone. L'annullamento non revoca un messaggio eventualmente già accettato dal servizio.

Non sono ancora implementati template, coda, fallback automatico e tracking.

## Retry policy

I retry applicativi usano Polly.Core e sono disattivati per default. Per abilitarli, aggiungere alla sezione `EmailOptions`:

```json
"Retry": {
  "MaxRetryAttempts": 2,
  "BaseDelay": "00:00:01"
}
```

`MaxRetryAttempts` indica i tentativi aggiuntivi: 2 significa al massimo 3 invii. Sono ammessi valori da 0 a 10; `BaseDelay` deve essere compreso tra zero e 30 secondi. Il backoff è esponenziale, con jitter e attesa massima di 30 secondi per retry quando il server non fornisce un `Retry-After` valido. Il token interrompe anche le attese. La configurazione viene validata con le altre opzioni.

La policy ritenta esclusivamente rifiuti transitori espliciti:

- SMTP: risposte 4xx con `SenderNotAccepted`, `RecipientNotAccepted` o `MessageNotAccepted`.
- SendGrid: HTTP 429.
- SES: codici `Throttling`, `ThrottlingException`, `TooManyRequestsException`.
- Azure: HTTP 429 nella richiesta iniziale. Dopo l'accettazione, il polling resta fuori dalla policy: un errore nel polling non avvia un nuovo invio.

Timeout, errori di rete, cancellazioni, errori permanenti e risposte generiche 5xx non provocano retry applicativi: il messaggio potrebbe essere già stato accettato. Questo non garantisce consegna esattamente una volta. Per SendGrid e Azure, un header `Retry-After` valido determina l'attesa: sono supportati secondi interi non negativi e date HTTP. Una data già trascorsa equivale a zero secondi. L'attesa indicata dal server prevale sul backoff, senza jitter né riduzione al limite di 30 secondi; il token può interromperla. Header assenti o malformati usano il backoff configurato. Se l'attesa supera la capacità dei timer .NET (circa 49 giorni), l'errore originale viene propagato senza ritentare prima della scadenza. L'header non rende ritentabili errori permanenti, esiti incerti o errori di polling Azure.

Quando la policy è attiva, i client SES e Azure creati dalla libreria disabilitano i propri retry, evitando la moltiplicazione dei tentativi. Con `MaxRetryAttempts = 0` restano i comportamenti preesistenti degli SDK. Client personalizzati forniti tramite override di `CreateClient` devono disabilitare autonomamente eventuali retry interni.

Ogni retry emette il log Warning `1002 / EmailSendRetry` con provider, numero del retry, attesa e TraceId; quando il tracing è attivo aggiunge l'evento `email.retry`. Le metriche `email.send.completed` e `email.send.duration` restano per chiamata logica. Nessun contenuto dell'email viene aggiunto ai log di retry.

## Logging e monitoring

Tutti i provider strumentano `Send` e `SendAsync` con log strutturati, metriche e tracing. Una chiamata produce un solo esito finale: `success`, `failure` o `cancelled`. `success` significa che la chiamata al provider è terminata senza errori, non che l'email sia stata consegnata. I retry sono inclusi nella stessa chiamata: durata ed esito finale comprendono tutti i tentativi e le attese.

### Log

`AddEmailServices` usa gli `ILogger<T>` dell'applicazione, se registrati. Negli host ASP.NET Core il logging è già disponibile; in un'applicazione con sola `ServiceCollection` occorre configurarlo tramite `AddLogging` e il provider di output desiderato. La libreria funziona anche senza logging configurato. Per costruzione manuale, ogni sender offre anche un costruttore con `IOptions<EmailOptions>` e `ILogger<TSender>`.

La categoria è il nome completo del sender, per esempio `FDR11.Notifications.Email.Concretes.SmtpEmailSender`. Una configurazione dell'applicazione può usare:

```json
{
  "Logging": {
    "LogLevel": {
      "FDR11.Notifications.Email": "Information"
    }
  }
}
```

| Evento | Livello | Campi |
| --- | --- | --- |
| 1000 / `EmailSendStarted` | Debug | `Provider`, `OperationId`, `TraceId` |
| 1001 / `EmailSendCompleted` | Information per successo/cancellazione; Error per errore | Campi iniziali, `Outcome`, `DurationSeconds`, `ErrorType` |

`OperationId` correla inizio e fine anche senza tracing attivo. `TraceId` è disponibile quando esiste un'attività. I log della libreria non includono mittente, destinatari, oggetto, corpo, allegati, credenziali, messaggi o stack trace delle eccezioni. `ErrorType` contiene solo il nome del tipo; l'eccezione originale viene propagata al chiamante.

### Metriche e tracing

Il nome del `Meter` e dell'`ActivitySource` è **FDR11.Notifications.Email**, esposto da `EmailDiagnostics.SourceName` nel namespace `FDR11.Notifications.Email.Diagnostics`.

| Metrica | Tipo | Unità | Tag |
| --- | --- | --- | --- |
| `email.send.completed` | Counter | `{send}` | `email.provider`, `email.outcome` |
| `email.send.duration` | Histogram | `s` | `email.provider`, `email.outcome` |
| `email.send.active` | UpDownCounter | `{send}` | `email.provider` |

I provider sono `SMTP`, `SendGrid`, `SES`, `Azure`; l'alias `AZ` viene riportato come `Azure`. Le metriche non usano identificativi di invio o indirizzi come tag. Gli strumenti sono condivisi nel processo; gli invii già annullati vengono contati come `cancelled`.

Lo span `email.send` ha tipo `Client`, eredita il trace chiamante e contiene provider ed esito. Gli errori impostano lo stato `Error` e il solo tag `error.type`; la cancellazione richiesta dal chiamante mantiene lo stato `Unset`. Un'eccezione di cancellazione senza token del chiamante annullato viene classificata come errore, ad esempio per un timeout del provider.

Per osservare le metriche locali, con `dotnet-counters` installato:

```shell
dotnet-counters monitor --process-id <PID> --counters FDR11.Notifications.Email
```

Per l'esportazione OpenTelemetry, l'applicazione deve aggiungere `OpenTelemetry.Extensions.Hosting` e l'exporter scelto. Nel suo `Program.cs`, registrare le sorgenti nella pipeline OpenTelemetry:

```csharp
using FDR11.Notifications.Email.Diagnostics;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;

builder.Services.AddOpenTelemetry()
    .WithTracing(tracing => tracing.AddSource(EmailDiagnostics.SourceName))
    .WithMetrics(metrics => metrics.AddMeter(EmailDiagnostics.SourceName));
```

Aggiungere l'exporter alla pipeline secondo il backend utilizzato: questa registrazione da sola non invia telemetria a un servizio esterno. La libreria non configura collector, dashboard, alert o sonde di consegna. Le metriche permettono di osservare volume, errori, latenze e invii attivi; le soglie operative dipendono dall'applicazione.

Riferimenti: [metriche .NET](https://learn.microsoft.com/en-us/dotnet/core/diagnostics/metrics-instrumentation) e [tracing con ActivitySource](https://learn.microsoft.com/en-us/dotnet/core/diagnostics/distributed-tracing-instrumentation-walkthroughs).

## Struttura del repository

```text
FDR11.Notifications.Email.slnx
Code/
  Abstracts/IEmailSender.cs
  Concretes/
    SmtpEmailSender.cs
    SendGridEmailSender.cs
    AwsEmailSender.cs       # contiene SesEmailSender
    AzureEmailSender.cs
  Diagnostics/EmailDiagnostics.cs
  Extensions/DI/EmailSenderExtensions.cs
  Models/
    EmailMessage.cs
    EmailAttachment.cs
  Options/
    EmailOptions.cs
    EmailOptionsValidator.cs
    EmailRetryOptions.cs
  Resilience/EmailRetry.cs
Tests/
  ProviderCompatibilityTests.cs
  ConsolidationTests.cs
  DiagnosticsTests.cs
  RetryTests.cs
  RetryAfterTests.cs
```

## Estendere la libreria

Un nuovo provider deve implementare entrambi i metodi di `IEmailSender`. Può essere registrato esplicitamente come `IEmailSender`; per renderlo selezionabile tramite `EmailOptions.Provider` occorre aggiornare lo switch in `EmailSenderExtensions`.

## Build, test e pacchetto

Eseguire dalla radice del repository con l'SDK .NET 10:

```shell
dotnet build FDR11.Notifications.Email.slnx
dotnet test Tests/FDR11.Notifications.Email.Tests.csproj
dotnet pack Code/FDR11.Notifications.Email.csproj --configuration Release --output artifacts
```

I test verificano selezione DI e validazione delle opzioni, mapping dei messaggi e allegati, cancellazione anticipata su tutti i provider, inoltro del token e gestione delle risposte SendGrid. Verificano inoltre log, correlazione, metriche, cancellazioni, errori, concorrenza e assenza di dati sensibili nella telemetria. Usano serializzazione MIME, client simulati e un handler HTTP in memoria: non inviano email reali e non verificano credenziali o consegna sui servizi remoti.

Il progetto dichiara `PackageId=FDR11.Notifications.Email`, include README e licenza, genera documentazione XML, PDB portabili e un pacchetto simboli `.snupkg`. SourceLink collega i sorgenti al repository GitHub. `CI=true` abilita `ContinuousIntegrationBuild`; i workflow passano la stessa `Version` a build e pack. Per configurazione dei feed, approvazione e versionamento vedere [RELEASING.md](https://github.com/della11/FDR11.Notifications.Email/blob/main/RELEASING.md).

## Roadmap di integrazione proposta

L'ordine seguente indica le dipendenze di lavoro; le caselle indicano lo stato di implementazione.

- [x] **Consolidamento:** validazione delle opzioni, registrazione Azure, correzioni di corpo/allegati/cancellazione, gestione delle risposte SendGrid e test su tutti i provider.
- [x] **Logging e monitoring:** correlazione, durata ed esito dei tentativi, senza credenziali o contenuto dei messaggi nei log.
- [x] **Retry policy (Polly):** errori transitori, attesa progressiva, limite tentativi e coordinamento con i retry degli SDK; gestione esplicita degli esiti incerti.
- [ ] **Template email (Razor / Scriban):** contratto di rendering separato dal trasporto e scelta di un primo motore.
- [ ] **Email queue (RabbitMQ / Kafka):** contratto di accodamento, worker, persistenza, gestione duplicati e messaggi falliti; eventuale outbox nell'applicazione chiamante.
- [ ] **Fallback provider automatico:** ordine configurabile, compatibilità delle funzionalità e regole che evitino reinvii automatici su esiti incerti.
- [ ] **Email tracking (open/click):** identificativi del provider, eventi correlati e integrazione webhook dedicata.

Prima milestone completata: consolidamento, logging/monitoring e retry. Coda e tracking richiedono anche componenti applicativi esterni alla libreria.
