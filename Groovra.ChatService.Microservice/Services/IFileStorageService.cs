namespace Groovra.ChatService.Microservice.Services;

public interface IFileStorageService
{
    // Повертає публічний URL завантаженого файлу (${PublicUrl}/${key}).
    Task<string> UploadFileAsync(Stream fileStream, string fileName, string contentType, CancellationToken cancellationToken);

    // Парсить ключ об'єкта з публічного URL (${PublicUrl}/${key}) і видаляє його з R2.
    // "Best effort" — мережеві/R2-помилки логуються, але не прокидаються назовні, щоб
    // видалення повідомлення в БД не блокувалось через тимчасову недоступність R2.
    Task DeleteFileAsync(string mediaUrl, CancellationToken cancellationToken);
}
