namespace Groovra.Music.Microservice.DTOs;

public class UploadTrackRequestDto
{
    public string Title { get; set; } = string.Empty;

    public string? ArtistName { get; set; } = string.Empty;

    public string? Album { get; set; }

    public string? Genre { get; set; }
    public string? Mood { get; set; }
    public Guid? TargetUserId { get; set; }
    public IFormFile File { get; set; } = null!;

    public IFormFile? CoverImage { get; set; }
}