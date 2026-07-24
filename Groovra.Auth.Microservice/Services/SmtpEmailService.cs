using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace Groovra.Auth.Microservice.Services;

/// <summary>
/// Sends mail through a generic SMTP server (e.g. a real production mailbox), configured via
/// the "Email:Smtp" section. Selected instead of <see cref="MailtrapEmailService"/> by setting
/// "Email:Provider" to "Smtp".
/// </summary>
public class SmtpEmailService : IEmailSender
{
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
        string BodyContent = "")
    {
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
                var secureOption = useSsl ? SecureSocketOptions.SslOnConnect : SecureSocketOptions.StartTls;
                await client.ConnectAsync(SmtpServer, SmtpPortNumber, secureOption);
                await client.AuthenticateAsync(_config["Email:Smtp:Username"], _config["Email:Smtp:Password"]);

                await client.SendAsync(mimeMessage);
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
