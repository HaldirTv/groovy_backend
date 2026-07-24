namespace Groovra.Auth.Microservice.Services;

// POCO-биндинги "Email:Mailtrap" и "Email:Brevo" через IOptions<T> — вместо того щоб
// кожен *EmailService читав IConfiguration за рядковими ключами напряму (крихко, легко
// одруку в шляху "Email:Host" замість "Email:Mailtrap:Host"). Реєструються в Program.cs
// через builder.Services.Configure<T>(...).
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

    // Brevo (як і більшість транзакційних SMTP-релеїв) вимагає, щоб адреса відправника
    // була заздалегідь верифікована в акаунті — довільний FromAddress з виклику
    // SendEmailAsync тут просто відхилиться сервером. Тому BrevoSmtpEmailService завжди
    // підставляє саме цю адресу як From, ігноруючи FromAddress-параметр виклику.
    public string FromEmail { get; set; } = string.Empty;
}
