using Groovra.Music.Microservice.Caching;

namespace Groovra.Music.Microservice.Services;

/// <summary>
/// Фоновая прогревающая джоба Hangfire: пересчитывает тяжёлые выборки (рекомендации
/// по настрою, топ треков) и кладёт готовый JSON в Redis, чтобы соответствующие
/// API-эндпоинты читали O(1) из кеша вместо SQL при каждом запросе.
/// </summary>
public class CacheWarmupService
{
    public const int DefaultRecommendationsTake = 8;
    public const int TrendingCacheSize = 100;

    private static readonly TimeSpan WarmCacheTtl = TimeSpan.FromMinutes(30);

    private readonly MusicService _musicService;
    private readonly ICacheService _cache;
    private readonly ILogger<CacheWarmupService> _logger;

    public CacheWarmupService(MusicService musicService, ICacheService cache, ILogger<CacheWarmupService> logger)
    {
        _musicService = musicService;
        _cache = cache;
        _logger = logger;
    }

    public async Task WarmUpAsync(CancellationToken cancellationToken = default)
    {
        await WarmMoodRecommendationsAsync(cancellationToken);
        await WarmTrendingAsync(cancellationToken);
    }

    private async Task WarmMoodRecommendationsAsync(CancellationToken cancellationToken)
    {
        var moodResults = await _musicService.GetMoodRecommendationsAsync(DefaultRecommendationsTake, cancellationToken);

        var payload = moodResults
            .Select(r => new CachedMoodGroup
            {
                Mood = r.Mood,
                Tracks = r.Tracks.Select(CachedTrack.FromTrack).ToList()
            })
            .ToList();

        await _cache.SetAsync(CacheKeys.RecommendationsMood(DefaultRecommendationsTake), payload, WarmCacheTtl, cancellationToken);
        _logger.LogInformation("Cache warmup: mood recommendations refreshed ({Count} categories).", payload.Count);
    }

    private async Task WarmTrendingAsync(CancellationToken cancellationToken)
    {
        var (items, totalCount) = await _musicService.GetPopularTracksAsync(1, TrendingCacheSize, cancellationToken);

        var payload = new CachedTrackPage
        {
            Items = items.Select(CachedTrack.FromTrack).ToList(),
            TotalCount = totalCount
        };

        await _cache.SetAsync(CacheKeys.TrendingTop, payload, WarmCacheTtl, cancellationToken);
        _logger.LogInformation("Cache warmup: trending tracks refreshed (top {Count} of {Total}).", payload.Items.Count, totalCount);
    }
}
