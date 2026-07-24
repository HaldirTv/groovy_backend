using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using Amazon.S3;
using Microsoft.Extensions.Options;

namespace Groovra.ChatService.Microservice.Services;

// Завантаження медіа (картинки/голосові/файли з чату) у Cloudflare R2.
//
// PutObject НЕ йде через AWSSDK.S3: сам SDK (4.0.101.4) під капотом підписує тіло запиту
// через STREAMING-...-PAYLOAD-TRAILER (chunked SigV4 із checksum-трейлером), якого R2 не
// підтримує — сервер повертає SignatureDoesNotMatch навіть на повністю коректний запит із
// правильними ключами. Ні DisablePayloadSigning, ні DisableDefaultChecksumValidation цю
// поведінку SDK не прибирають (перевірено емпірично: ті самі AccessKeyId/SecretAccessKey
// проходять на ура при ручному нечанкованому SigV4-запиті поза SDK). Тому PUT робимо самі:
// один звичайний (не chunked) запит з X-Amz-Content-Sha256: UNSIGNED-PAYLOAD і ручним підписом.
// DeleteObject лишається на AWSSDK.S3 — DELETE не має тіла запиту, і ця проблема на нього не поширюється.
public class FileStorageService : IFileStorageService
{
    private readonly HttpClient _httpClient;
    private readonly IAmazonS3 _s3Client;
    private readonly CloudflareR2Options _options;
    private readonly ILogger<FileStorageService> _logger;

    public FileStorageService(HttpClient httpClient, IAmazonS3 s3Client, IOptions<CloudflareR2Options> options, ILogger<FileStorageService> logger)
    {
        _httpClient = httpClient;
        _s3Client = s3Client;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<string> UploadFileAsync(Stream fileStream, string fileName, string contentType, CancellationToken cancellationToken)
    {
        // Guid-префікс — щоб однакові імена файлів від різних юзерів не перезаписували
        // одне одного в бакеті; розширення лишаємо для читабельності URL/змісту в браузері.
        var safeExtension = Path.GetExtension(fileName);
        var key = $"{Guid.NewGuid()}{safeExtension}";

        using var buffer = new MemoryStream();
        await fileStream.CopyToAsync(buffer, cancellationToken);
        var bodyBytes = buffer.ToArray();

        var host = new Uri(_options.ServiceUrl).Host;
        var canonicalUri = $"/{_options.BucketName}/{key}";
        var now = DateTime.UtcNow;
        var amzDate = now.ToString("yyyyMMddTHHmmssZ", CultureInfo.InvariantCulture);
        var dateStamp = now.ToString("yyyyMMdd", CultureInfo.InvariantCulture);
        const string payloadHashPlaceholder = "UNSIGNED-PAYLOAD";

        var canonicalHeaders = $"host:{host}\nx-amz-content-sha256:{payloadHashPlaceholder}\nx-amz-date:{amzDate}\n";
        const string signedHeaders = "host;x-amz-content-sha256;x-amz-date";
        var canonicalRequest = $"PUT\n{canonicalUri}\n\n{canonicalHeaders}\n{signedHeaders}\n{payloadHashPlaceholder}";

        const string algorithm = "AWS4-HMAC-SHA256";
        var credentialScope = $"{dateStamp}/auto/s3/aws4_request";
        var stringToSign = $"{algorithm}\n{amzDate}\n{credentialScope}\n{Sha256Hex(canonicalRequest)}";

        var kDate = HmacSha256(Encoding.UTF8.GetBytes($"AWS4{_options.SecretAccessKey}"), dateStamp);
        var kRegion = HmacSha256(kDate, "auto");
        var kService = HmacSha256(kRegion, "s3");
        var kSigning = HmacSha256(kService, "aws4_request");
        var signature = Convert.ToHexStringLower(HmacSha256(kSigning, stringToSign));

        var authorization = $"{algorithm} Credential={_options.AccessKeyId}/{credentialScope}, SignedHeaders={signedHeaders}, Signature={signature}";

        using var request = new HttpRequestMessage(HttpMethod.Put, $"https://{host}{canonicalUri}")
        {
            Content = new ByteArrayContent(bodyBytes),
        };
        request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(
            string.IsNullOrWhiteSpace(contentType) ? "application/octet-stream" : contentType);
        request.Headers.TryAddWithoutValidation("x-amz-content-sha256", payloadHashPlaceholder);
        request.Headers.TryAddWithoutValidation("x-amz-date", amzDate);
        request.Headers.TryAddWithoutValidation("Authorization", authorization);

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            _logger.LogError("Cloudflare R2 upload failed: {Status} {Body}", response.StatusCode, body);
            throw new InvalidOperationException($"Не вдалося завантажити файл у сховище (R2 status {(int)response.StatusCode}).");
        }

        return $"{_options.PublicUrl.TrimEnd('/')}/{key}";
    }

    public async Task DeleteFileAsync(string mediaUrl, CancellationToken cancellationToken)
    {
        var prefix = $"{_options.PublicUrl.TrimEnd('/')}/";
        if (string.IsNullOrWhiteSpace(mediaUrl) || !mediaUrl.StartsWith(prefix, StringComparison.Ordinal))
        {
            // Не наш бакет (напр. дані, що не проходили через UploadFileAsync) — нема що видаляти.
            return;
        }

        var key = mediaUrl[prefix.Length..];
        if (string.IsNullOrWhiteSpace(key))
            return;

        try
        {
            await _s3Client.DeleteObjectAsync(_options.BucketName, key, cancellationToken);
        }
        catch (AmazonS3Exception ex)
        {
            // Файл уже видалено/недоступний — не блокуємо видалення повідомлення в БД через це.
            _logger.LogWarning(ex, "Не вдалося видалити файл {Key} з Cloudflare R2", key);
        }
    }

    private static string Sha256Hex(string input)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexStringLower(bytes);
    }

    private static byte[] HmacSha256(byte[] key, string data)
    {
        using var hmac = new HMACSHA256(key);
        return hmac.ComputeHash(Encoding.UTF8.GetBytes(data));
    }
}
