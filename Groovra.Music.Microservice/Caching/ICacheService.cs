namespace Groovra.Music.Microservice.Caching;

/// <summary>
/// Тонкая обёртка над StackExchange.Redis для Music.Microservice. Сознательно не
/// IDistributedCache — тому урезанному интерфейсу негде взять RemoveByPatternAsync
/// (сброс всех "music:tracks:search:*" одним махом при изменении каталога).
/// </summary>
public interface ICacheService
{
    Task<T?> GetAsync<T>(string key, CancellationToken cancellationToken = default) where T : class;
    Task SetAsync<T>(string key, T value, TimeSpan ttl, CancellationToken cancellationToken = default) where T : class;
    Task RemoveAsync(string key, CancellationToken cancellationToken = default);
    Task RemoveByPatternAsync(string pattern, CancellationToken cancellationToken = default);

    /// <summary>
    /// Атомарно ставит ключ-замок только если его ещё нет (SET NX + TTL). Возвращает true,
    /// если замок захвачен именно этим вызовом. Используется, чтобы при холодном кеше только
    /// ОДИН из пачки одновременных запросов поставил фоновую джобу прогрева, а не все сразу
    /// (иначе тяжёлый SQL под RESOURCE_SEMAPHORE запустился бы десятками параллельно).
    /// При недоступности Redis возвращает false — тогда прогрев просто не ставится, но запрос
    /// всё равно отдаёт быстрый пустой ответ.
    /// </summary>
    Task<bool> TryAcquireLockAsync(string key, TimeSpan ttl, CancellationToken cancellationToken = default);
}
