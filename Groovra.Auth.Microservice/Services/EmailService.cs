using MailKit.Net.Smtp;
using MimeKit;
using Microsoft.Extensions.Configuration;
using Org.BouncyCastle.Asn1.Cmp;

namespace Groovra.Auth.Microservice.Services;

public class EmailService
{
    private readonly IConfiguration _config;

    public EmailService(IConfiguration config)
    {
        _config = config;
    }

    public async Task sendEmailAsync(
        string FromAddress = "support@groovra.com",
        string FromAdressTitle = "Groovra Support",
        string ToAddress = "support@groovra.com",
        string ToAdressTitle = "Groovra User",
        string Subject = "",
        string BodyContent="")
    {
        try
        {
  
            
            // From Address    
            /*string FromAddress = "support@groovra.com";  
            string FromAdressTitle = "Groovra Support";  
            
            // To Address    
            string ToAddress = email;  
            string ToAdressTitle = "Groovra User";  
            
            string Subject = "Сброс пароля в Groovra"; */ 
            

  
            string SmtpServer = _config["Email:Host"]!;  
            int SmtpPortNumber = int.Parse(_config["Email:Port"]!);  

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
        
                await client.ConnectAsync(SmtpServer, SmtpPortNumber, MailKit.Security.SecureSocketOptions.StartTls);  
                await client.AuthenticateAsync(_config["Email:Username"], _config["Email:Password"]);  
                
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