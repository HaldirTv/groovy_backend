using MailKit.Net.Smtp;
using Microsoft.Extensions.Options;
using MimeKit;

namespace Groovra.Auth.Microservice.Services;

public class MailtrapEmailService : IEmailSender
{
    private static readonly TimeSpan SmtpTimeout = TimeSpan.FromSeconds(15);

    private readonly MailtrapOptions _options;

    public MailtrapEmailService(IOptions<MailtrapOptions> options)
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
        // Див. коментар у BrevoSmtpEmailService: без бюджету часу зависання SMTP переростає
        // у 504 від гейтвею замість зрозумілої помилки.
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(SmtpTimeout);
        var ct = timeoutCts.Token;

        try
        {
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

                await client.ConnectAsync(_options.Host, _options.Port, MailKit.Security.SecureSocketOptions.StartTls, ct);
                await client.AuthenticateAsync(_options.Username, _options.Password, ct);

                await client.SendAsync(mimeMessage, ct);

                Console.WriteLine("The mail has been sent successfully !!");
                await client.DisconnectAsync(true, ct);
            }
        }
        catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"SMTP-надсилання через {_options.Host}:{_options.Port} не вклалося у {SmtpTimeout.TotalSeconds:0}с.");
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex.Message);
            throw;
        }
    }
}
