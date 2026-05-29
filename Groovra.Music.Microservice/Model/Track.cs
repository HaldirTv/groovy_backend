namespace Groovra.Music.Microservice.Model;

public class Track
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Title { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty; // Путь к файлу на диске
    public DateTime UploadedAt { get; set; } = DateTime.UtcNow;
}