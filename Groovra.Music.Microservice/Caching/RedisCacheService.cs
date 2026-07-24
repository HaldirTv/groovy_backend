using System.Text.Json;
using StackExchange.Redis;

namespace Groovra.Music.Microservice.Caching;

public class RedisCacheService : ICacheService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IConnectionMultiplexer _multiplexer;
    private readonly ILogger<RedisCacheService> _logger;

    public RedisCacheService(IConnectionMultiplexer multiplexer, ILogger<RedisCacheService> logger)
    {
        _multiplexer = multiplexer;
        _logger = logger;
    }

    private IDatabase Db => _multiplexer.GetDatabase();

    public async Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default) where T : class
    {
        try
        {
            var value = await Db.StringGetAsync(key);
            if (value.IsNullOrEmpty) return null;

            return JsonSerializer.Deserialize<T>((string)value!, JsonOptions);
        }
        catch (Exception ex)
        {
            // Redis недоступен/сбойнул — это не должно валить запрос, просто считаем промахом
            // кеша и даём вызывающему коду упасть обратно на БД (cache-aside).
            _logger.LogWarning(ex, "Cache GET failed for key {Key}, falling back to source.", key);
            return null;
        }
    }

    public async Task SetAsync<T>(string key, T value, TimeSpan ttl, CancellationToken cancellationToken = default) where T : class
    {
        try
        {
            var json = JsonSerializer.Serialize(value, JsonOptions);
            await Db.StringSetAsync(key, json, ttl);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Cache SET failed for key {Key}.", key);
        }
    }

    public async Task RemoveAsync(string key, CancellationToken cancellationToken = default)
    {
        try
        {
            await Db.KeyDeleteAsync(key);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Cache REMOVE failed for key {Key}.", key);
        }
    }

    public async Task<bool> TryAcquireLockAsync(string key, TimeSpan ttl, CancellationToken cancellationToken = default)
    {
        try
        {
            // When.NotExists == Redis SET NX: атомарно, без гонки между несколькими инстансами.
            return await Db.StringSetAsync(key, "1", ttl, When.NotExists);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Cache lock acquire failed for key {Key}.", key);
            return false;
        }
    }

    public async Task RemoveByPatternAsync(string pattern, CancellationToken cancellationToken = default)
    {
        try
        {
            var endpoint = _multiplexer.GetEndPoints().First();
            var server = _multiplexer.GetServer(endpoint);

            var keys = new List<RedisKey>();
            await foreach (var key in server.KeysAsync(pattern: pattern).WithCancellation(cancellationToken))
            {
                keys.Add(key);
            }

            if (keys.Count > 0)
            {
                await Db.KeyDeleteAsync(keys.ToArray());
                _logger.LogInformation("Cache invalidation: removed {Count} keys matching '{Pattern}'.", keys.Count, pattern);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Cache pattern removal failed for pattern {Pattern}.", pattern);
        }
    }
}
