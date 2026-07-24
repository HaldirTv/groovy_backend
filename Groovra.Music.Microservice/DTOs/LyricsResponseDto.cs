namespace Groovra.Music.Microservice.DTOs;

public class LyricsResponseDto
{
    public Guid TrackId { get; init; }
    public string Title { get; init; } = string.Empty;
    public string Artist { get; init; } = string.Empty;
    public string Lrc { get; init; } = string.Empty;
    public bool IsInstrumental { get; init; }
}
