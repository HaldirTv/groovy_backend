namespace Groovra.Music.Microservice.Caching;

/// <summary>
/// Обгортка cache-aside сторінки результатів для DTO, які вже плоскі й серіалізуються
/// напряму (без навігаційних властивостей EF) — Artists/Albums/Playlists search. На
/// відміну від Tracks (<see cref="CachedTrackPage"/>), тут не потрібна окрема
/// Cached-проекція: AlbumListItemDto/PlaylistListItemDto/ArtistListItemDto самі по собі
/// безпечні для System.Text.Json.
/// </summary>
public sealed class CachedPage<T>
{
    public List<T> Items { get; set; } = [];
    public int TotalCount { get; set; }
}
