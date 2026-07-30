namespace Groovra.Music.Microservice.DTOs;

public class ArtistStatsDto
{
    public long TotalPlayCount { get; set; }

    public int TotalAlbumLikes { get; set; }

    public List<TopTrackDto> TopTracks { get; set; } = new();
}

public class TopTrackDto
{
    public Guid TrackId { get; set; }
    public string Title { get; set; } = string.Empty;
    public long PlayCount { get; set; }
    public string? CoverImageUrl { get; set; }
}

public class GlobalStatsDto
{
    public int AiMixesCount { get; set; }
    public int SongsCount { get; set; }
    public int AlbumsCount { get; set; }
    public int HoursListened { get; set; }
}