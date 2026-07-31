using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Groovra.Auth.Microservice.Services;

/// <summary>Надсилає пошту через Brevo Transactional Email API поверх HTTPS (443) замість SMTP.
/// Причина існування: на DigitalOcean вихідні SMTP-порти (25/465/587) за замовчуванням мовчки
/// дропаються, тому ConnectAsync у SMTP-транспорті висить ~2 хв і клієнт отримує 504 від
/// гейтвею (YARP ActivityTimeout = 100с). Порт 443 не блокується, тому цей транспорт працює.</summary>
public class BrevoApiEmailService : IEmailSender
{
    public const string HttpClientName = "BrevoApi";

    private readonly BrevoApiOptions _options;
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<BrevoApiEmailService> _logger;

    public BrevoApiEmailService(
        IOptions<BrevoApiOptions> options,
        IHttpClientFactory httpClientFactory,
        ILogger<BrevoApiEmailService> logger)
    {
        _options = options.Value;
        _httpClientFactory = httpClientFactory;
        _logger = logger;
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
        if (string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            throw new InvalidOperationException(
                "Email:BrevoApi:ApiKey не налаштовано - надіслати лист неможливо. " +
                "Додайте BREVO_API_KEY у .env на сервері.");
        }

        var effectiveFromAddress = string.IsNullOrWhiteSpace(_options.FromEmail)
            ? FromAddress
            : _options.FromEmail;
        var effectiveFromName = string.IsNullOrWhiteSpace(_options.FromName)
            ? FromAdressTitle
            : _options.FromName;

        var payload = new
        {
            sender = new { name = effectiveFromName, email = effectiveFromAddress },
            to = new[] { new { email = ToAddress, name = ToAdressTitle } },
            subject = Subject,
            htmlContent = BodyContent
        };

        var client = _httpClientFactory.CreateClient(HttpClientName);
        using var request = new HttpRequestMessage(HttpMethod.Post, "/v3/smtp/email")
        {
            Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json")
        };
        request.Headers.Add("api-key", _options.ApiKey);
        request.Headers.Add("accept", "application/json");

        using var response = await client.SendAsync(request, cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            // Тіло відповіді Brevo містить машинну причину (напр. незверифікований відправник),
            // без неї діагностувати збій відправки в проді практично неможливо.
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogError("Brevo API повернув {StatusCode}: {Body}", (int)response.StatusCode, body);
            throw new InvalidOperationException(
                $"Brevo API відхилив надсилання листа ({(int)response.StatusCode}): {body}");
        }

        _logger.LogInformation("Лист успішно надіслано через Brevo API на {ToAddress}", ToAddress);
    }
}
