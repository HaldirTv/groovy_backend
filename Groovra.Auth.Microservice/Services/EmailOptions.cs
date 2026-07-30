namespace Groovra.Auth.Microservice.Services;

public class MailtrapOptions
{
    public string Host { get; set; } = "sandbox.smtp.mailtrap.io";
    public int Port { get; set; } = 2525;
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
}

public class BrevoOptions
{
    public string Host { get; set; } = "smtp-relay.brevo.com";
    public int Port { get; set; } = 587;
    public string Username { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;

    public string FromEmail { get; set; } = string.Empty;
}
