using System.Security.Cryptography;
using System.Text;

namespace Groovra.Music.Microservice.Caching;

/// <summary>Централизованные ключи/паттерны Redis-кеша Music.Microservice (префикс "music:").</summary>
public static class CacheKeys
{
    public const string Genres = "music:genres";
    public const string TrendingTop = "music:trending:top100";

    /// <summary>Замок-дебаунсер фоновой джобы прогрева: пока он стоит, повторные промахи
    /// кеша не ставят новые джобы (защита от лавины одинаковых тяжёлых пересчётов).</summary>
    public const string WarmupLock = "music:warmup:lock";

    public const string SearchPatternAll = "music:tracks:search:*";
    public const string TrendingPatternAll = "music:trending:*";
    public const string RecommendationsPatternAll = "music:recommendations:*";
    public const string ArtistsSearchPatternAll = "music:artists:search:*";
    public const string AlbumsSearchPatternAll = "music:albums:search:*";
    public const string PlaylistsSearchPatternAll = "music:playlists:search:*";

    /// <summary>Паттерны, которые нужно сбросить при любом изменении каталога треков
    /// (добавление/удаление/восстановление/переименование). Артисты сюда входят, бо їхній
    /// список — це агрегація саме над треками (ArtistsController рахує TrackCount/обкладинку
    /// з таблиці Tracks).</summary>
    public static readonly string[] ListPatterns =
        [SearchPatternAll, TrendingPatternAll, RecommendationsPatternAll, ArtistsSearchPatternAll];

    public static string Track(Guid id) => $"music:track:{id}";

    public static string RecommendationsMood(int take) => $"music:recommendations:mood:{take}";

    public static string TracksSearch(string? search, Guid? userId, string? genre, string? artist, int pageNumber, int pageSize)
    {
        var raw = $"{search}|{userId}|{genre}|{artist}|{pageNumber}|{pageSize}";
        var hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(raw)));
        return $"music:tracks:search:{hash}";
    }

    public static string ArtistsSearch(string? search, int pageNumber, int pageSize)
    {
        var raw = $"{search}|{pageNumber}|{pageSize}";
        var hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(raw)));
        return $"music:artists:search:{hash}";
    }

    /// <summary>artistUserId — власник ("мої альбоми"), а не поточний користувач-глядач:
    /// це легітимна частина того, ЩО шукається, тому безпечно ділити кеш між усіма, хто
    /// дивиться той самий зріз. IsLiked поточного глядача в кеш НЕ потрапляє — накладається
    /// на результат (з кеша чи з БД) окремо, уже після читання (див. AlbumsController).</summary>
    public static string AlbumsSearch(string? search, Guid? artistUserId, string? genre, int pageNumber, int pageSize)
    {
        var raw = $"{search}|{artistUserId}|{genre}|{pageNumber}|{pageSize}";
        var hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(raw)));
        return $"music:albums:search:{hash}";
    }

    /// <summary>Публічний пошук плейлистів (GET /music/playlists/search) — IsLiked так само
    /// накладається окремо після читання, у кеш не потрапляє (див. PlaylistsController).</summary>
    public static string PlaylistsSearch(string? search, int pageNumber, int pageSize)
    {
        var raw = $"{search}|{pageNumber}|{pageSize}";
        var hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(raw)));
        return $"music:playlists:search:{hash}";
    }
}
