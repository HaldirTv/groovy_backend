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

/// <summary>Налаштування транспорту Brevo через HTTP API (https://api.brevo.com, порт 443)
/// замість SMTP. Потрібен там, де хостинг блокує вихідні SMTP-порти - зокрема DigitalOcean
/// за замовчуванням мовчки дропає 25/465/587, через що SMTP-надсилання висить до таймауту.</summary>
public class BrevoApiOptions
{
    public string BaseUrl { get; set; } = "https://api.brevo.com";
    public string ApiKey { get; set; } = string.Empty;
    public string FromEmail { get; set; } = string.Empty;
    public string FromName { get; set; } = "Groovra";
}
