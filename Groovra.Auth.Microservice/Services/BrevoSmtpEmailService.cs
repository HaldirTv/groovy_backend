using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Options;
using MimeKit;

namespace Groovra.Auth.Microservice.Services;

public class BrevoSmtpEmailService : IEmailSender
{
    private static readonly TimeSpan SmtpTimeout = TimeSpan.FromSeconds(15);

    private readonly BrevoOptions _options;

    public BrevoSmtpEmailService(IOptions<BrevoOptions> options)
    {
        _options = options.Value;
    }

    public async Task SendEmailAsync(
        string FromAddress = "support@groovra.com",
        string FromAdressTitle = "Groovra Support",
        string ToAddress = "support@groovra.com",
        string ToAdressTitle = "Groovra User",
        string Subject = "",
        string BodyContent = "",
        CancellationToken cancellationToken = default)
    {
        // Жорсткий бюджет часу на всю SMTP-операцію. Без нього ConnectAsync висить на TCP-таймауті
        // ОС (~2 хв, якщо пакети мовчки дропаються фаєрволом), що довше за 100с ActivityTimeout
        // YARP - клієнт отримував 504 від гейтвею замість зрозумілої помилки.
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(SmtpTimeout);
        var ct = timeoutCts.Token;

        try
        {
            var effectiveFromAddress = string.IsNullOrWhiteSpace(_options.FromEmail)
                ? FromAddress
                : _options.FromEmail;

            var mimeMessage = new MimeMessage();
            mimeMessage.From.Add(new MailboxAddress(FromAdressTitle, effectiveFromAddress));
            mimeMessage.To.Add(new MailboxAddress(ToAdressTitle, ToAddress));
            mimeMessage.Subject = Subject;

            mimeMessage.Body = new TextPart("html")
            {
                Text = BodyContent
            };

            using (var client = new SmtpClient())
            {
                client.Timeout = (int)SmtpTimeout.TotalMilliseconds;

                await client.ConnectAsync(_options.Host, _options.Port, SecureSocketOptions.StartTls, ct);
                await client.AuthenticateAsync(_options.Username, _options.Password, ct);

                await client.SendAsync(mimeMessage, ct);

                Console.WriteLine("The mail has been sent successfully via Brevo!!");
                await client.DisconnectAsync(true, ct);
            }
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            // Найчастіша причина в проді - провайдер блокує вихідний SMTP (DigitalOcean дропає
            // 25/465/587 за замовчуванням). Кидаємо явну помилку, щоб виклик не висів до 504.
            throw new TimeoutException(
                $"SMTP-надсилання через {_options.Host}:{_options.Port} не вклалося у {SmtpTimeout.TotalSeconds:0}с. " +
                "Найімовірніше, вихідний SMTP-порт заблоковано хостингом - використайте Email:Provider=BrevoApi (HTTPS).");
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex.Message);
            throw;
        }
    }
}
