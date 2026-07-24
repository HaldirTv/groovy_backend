namespace Groovra.ChatService.Microservice.Services;

// Прив'язується до секції "CloudflareR2" в appsettings.Development.json (локально)
// / змінних середовища в docker-compose (продакшн-подібне середовище).
public class CloudflareR2Options
{
    public string AccountId { get; set; } = string.Empty;
    public string AccessKeyId { get; set; } = string.Empty;
    public string SecretAccessKey { get; set; } = string.Empty;
    public string BucketName { get; set; } = string.Empty;
    public string PublicUrl { get; set; } = string.Empty;
    public string ServiceUrl { get; set; } = string.Empty;
}
