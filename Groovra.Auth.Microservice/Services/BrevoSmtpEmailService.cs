using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Options;
using MimeKit;

namespace Groovra.Auth.Microservice.Services;

// Production-провайдер: реальні листи через Brevo SMTP-релей. Обирається через
// "Email:Provider" = "Brevo" (див. Program.cs). Той самий IEmailSender-контракт, що й
// MailtrapEmailService/SmtpEmailService — щоб ReglogService (та будь-який інший
// споживач) продовжував працювати без жодної зміни на своєму боці (Strategy pattern).
public class BrevoSmtpEmailService : IEmailSender
{
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
        string BodyContent = "")
    {
        try
        {
            // Brevo вимагає, щоб адреса відправника була верифікована в акаунті —
            // довільний FromAddress з виклику тут просто відхилиться сервером, тому
            // завжди підставляємо сконфігуровану адресу, якщо вона задана.
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
                await client.ConnectAsync(_options.Host, _options.Port, SecureSocketOptions.StartTls);
                await client.AuthenticateAsync(_options.Username, _options.Password);

                await client.SendAsync(mimeMessage);

                Console.WriteLine("The mail has been sent successfully via Brevo!!");
                await client.DisconnectAsync(true);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine(ex.Message);
            throw;
        }
    }
}
