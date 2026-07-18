namespace Groovra.Music.Microservice.DTOs;

public record AddDownloadDto(
    string Type,          
    Guid? ItemId,          
    string? AlbumName,      
    string? ArtistName      
);

public class DownloadItemDto
{
    public string Type { get; set; } = string.Empty; 
    public Guid? ItemId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string? SubTitle { get; set; }         
    public string? CoverImageUrl { get; set; }
    public int TrackCount { get; set; }
    public double TotalDurationSeconds { get; set; }
    public long FileSizeBytes { get; set; }
    public string? AudioUrl { get; set; }         
    public DateTime DownloadedAt { get; set; }
}