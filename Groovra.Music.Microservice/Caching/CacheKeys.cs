using System.Security.Cryptography;
using System.Text;

namespace Groovra.Music.Microservice.Caching;

public static class CacheKeys
{
    public const string Genres = "music:genres";
    public const string TrendingTop = "music:trending:top100";

    public const string WarmupLock = "music:warmup:lock";

    public const string SearchPatternAll = "music:tracks:search:*";
    public const string TrendingPatternAll = "music:trending:*";
    public const string RecommendationsPatternAll = "music:recommendations:*";
    public const string ArtistsSearchPatternAll = "music:artists:search:*";
    public const string AlbumsSearchPatternAll = "music:albums:search:*";
    public const string PlaylistsSearchPatternAll = "music:playlists:search:*";

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

    public static string AlbumsSearch(string? search, Guid? artistUserId, string? genre, int pageNumber, int pageSize)
    {
        var raw = $"{search}|{artistUserId}|{genre}|{pageNumber}|{pageSize}";
        var hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(raw)));
        return $"music:albums:search:{hash}";
    }

    public static string PlaylistsSearch(string? search, int pageNumber, int pageSize)
    {
        var raw = $"{search}|{pageNumber}|{pageSize}";
        var hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(raw)));
        return $"music:playlists:search:{hash}";
    }
}
