using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace Groovra.Auth.Microservice.Services;

public class SmtpEmailService : IEmailSender
{
    private static readonly TimeSpan SmtpTimeout = TimeSpan.FromSeconds(15);

    private readonly IConfiguration _config;

    public SmtpEmailService(IConfiguration config)
    {
        _config = config;
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
        // Див. коментар у BrevoSmtpEmailService: без бюджету часу зависання SMTP переростає
        // у 504 від гейтвею замість зрозумілої помилки.
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(SmtpTimeout);
        var ct = timeoutCts.Token;

        try
        {
            string SmtpServer = _config["Email:Smtp:Host"]!;
            int SmtpPortNumber = int.Parse(_config["Email:Smtp:Port"]!);
            bool useSsl = bool.TryParse(_config["Email:Smtp:EnableSsl"], out var parsedSsl) && parsedSsl;

            var mimeMessage = new MimeMessage();
            mimeMessage.From.Add(new MailboxAddress(FromAdressTitle, FromAddress));
            mimeMessage.To.Add(new MailboxAddress(ToAdressTitle, ToAddress));
            mimeMessage.Subject = Subject;

            mimeMessage.Body = new TextPart("html")
            {
                Text = BodyContent
            };

            using (var client = new SmtpClient())
            {
                client.Timeout = (int)SmtpTimeout.TotalMilliseconds;

                var secureOption = useSsl ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTls;
                await client.ConnectAsync(SmtpServer, SmtpPortNumber, secureOption, ct);
                await client.AuthenticateAsync(_config["Email:Smtp:Username"], _config["Email:Smtp:Password"], ct);

                await client.SendAsync(mimeMessage, ct);
                await client.DisconnectAsync(true, ct);
            }
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"SMTP-надсилання через {_config["Email:Smtp:Host"]}:{_config["Email:Smtp:Port"]} не вклалося у {SmtpTimeout.TotalSeconds:0}с.");
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex.Message);
            throw;
        }
    }
}
