namespace Groovra.Music.Microservice.Caching;

public sealed class CachedPage<T>
{
    public List<T> Items { get; set; } = [];
    public int TotalCount { get; set; }
}
