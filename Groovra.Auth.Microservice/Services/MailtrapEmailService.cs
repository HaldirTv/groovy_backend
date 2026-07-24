using MailKit.Net.Smtp;
using Microsoft.Extensions.Options;
using MimeKit;

namespace Groovra.Auth.Microservice.Services;

// Sandbox-провайдер для локальної розробки (Mailtrap перехоплює листи, не надсилає їх
// реальним отримувачам). Обирається через "Email:Provider" = "Mailtrap" (значення за
// замовчуванням, якщо Provider не вказано взагалі — див. Program.cs).
public class MailtrapEmailService : IEmailSender
{
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
        string BodyContent = "")
    {
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
                await client.ConnectAsync(_options.Host, _options.Port, MailKit.Security.SecureSocketOptions.StartTls);
                await client.AuthenticateAsync(_options.Username, _options.Password);

                await client.SendAsync(mimeMessage);

                Console.WriteLine("The mail has been sent successfully !!");
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
