namespace Groovra.Music.Microservice.DTOs;

/// <summary>
/// DTO for the audio track upload request.
/// The audio file is passed via IFormFile (multipart/form-data).
/// </summary>
public class UploadTrackRequestDto
{
    /// <summary>Title of the track.</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>Artist name (display name, not tied to Artist profile yet).</summary>
    public string ArtistName { get; set; } = string.Empty;

    /// <summary>Optional album name.</summary>
    public string? Album { get; set; }

    /// <summary>Genre tag (e.g. "Hip-Hop", "Electronic").</summary>
    public string? Genre { get; set; }
    
    public Guid? TargetUserId { get; set; }
    
    /// <summary>
    /// Audio file sent as multipart/form-data.
    /// Accepted MIME types: audio/mpeg, audio/wav, audio/ogg, audio/flac.
    /// </summary>
    public IFormFile File { get; set; } = null!;

    /// <summary>
    /// Optional cover image sent alongside the track.
    /// </summary>
    public IFormFile? CoverImage { get; set; }
}