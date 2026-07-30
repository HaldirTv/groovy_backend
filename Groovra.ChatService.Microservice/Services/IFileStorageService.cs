namespace Groovra.ChatService.Microservice.Services;

public interface IFileStorageService
{
    Task<string> UploadFileAsync(Stream fileStream, string fileName, string contentType, CancellationToken cancellationToken);

    Task DeleteFileAsync(string mediaUrl, CancellationToken cancellationToken);
}
