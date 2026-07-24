namespace Groovra.Music.Microservice.DTOs;

public class ArtistStatsDto
{
    /// <summary>Суммарное количество прослушиваний всех треков артиста</summary>
    public long TotalPlayCount { get; set; }

    /// <summary>Количество лайков на всех альбомах артиста</summary>
    public int TotalAlbumLikes { get; set; }

    /// <summary>Топ самых популярных треков</summary>
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