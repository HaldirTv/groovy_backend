using Groovra.Music.Microservice.Caching;
using Groovra.Music.Microservice.DTOs;
using Groovra.Music.Microservice.Model;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Groovra.Music.Microservice.Controllers;

/// <summary>
/// Публичный каталог артистов, агрегированный по существующим трекам/альбомам (без
/// отдельной таблицы артистов на стороне Music — профиль артиста живёт в Auth-сервисе).
/// Base route: /music/artists
/// </summary>
[ApiController]
[Route("music/artists")]
[Produces("application/json")]
public class ArtistsController : ControllerBase
{
    private readonly MusicDbContext _db;
    private readonly ICacheService _cache;

    public ArtistsController(MusicDbContext db, ICacheService cache)
    {
        _db = db;
        _cache = cache;
    }

    [HttpGet]
    [HttpGet("search")]
    public async Task<IActionResult> GetArtists(
        [FromQuery] string? search,
        [FromQuery] int pageNumber = 1,
        [FromQuery] int pageSize = 10,
        CancellationToken cancellationToken = default)
    {
        if (pageNumber < 1) pageNumber = 1;
        if (pageSize < 1) pageSize = 10;
        if (pageSize > 100) pageSize = 100;

        // Список артистів не персоналізований (немає IsLiked/currentUser) — усе, що
        // повертається, безпечно кешувати цілком і ділити між усіма запитами.
        var cacheKey = CacheKeys.ArtistsSearch(search, pageNumber, pageSize);
        var cached = await _cache.GetAsync<CachedPage<ArtistListItemDto>>(cacheKey, cancellationToken);
        if (cached is not null)
            return Ok(new PagedArtistResultDto(cached.Items, cached.TotalCount, pageNumber, pageSize));

        var baseUrl = $"{Request.Scheme}://{Request.Host}";

        // Групування/агрегати (Count/Sum) рахує СИЛЬНО SQL Server через GROUP BY — на
        // відміну від попередньої версії, яка тягнула в пам'ять застосунку ВСІ рядки
        // Tracks, що збіглися з пошуком, і групувала їх у C#. Це й було основним джерелом
        // великих memory grant'ів (RESOURCE_SEMAPHORE waits на цій БД).
        var artistsQuery = _db.Tracks.AsNoTracking().AsQueryable();
        if (!string.IsNullOrWhiteSpace(search))
            artistsQuery = artistsQuery.Where(t => t.ArtistName.Contains(search));

        var grouped = artistsQuery
            .GroupBy(t => t.ArtistName)
            .Select(g => new
            {
                Name = g.Key,
                TrackCount = g.Count(),
                TotalPlayCount = g.Sum(t => t.PlayCount),
            });

        var totalCount = await grouped.CountAsync(cancellationToken);

        var page = await grouped
            .OrderByDescending(g => g.TotalPlayCount)
            .ThenBy(g => g.Name)
            .Skip((pageNumber - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        // Обкладинку (трек з найбільшим PlayCount) і кількість альбомів тягнемо ЛИШЕ для
        // артистів поточної сторінки (типово ≤100 імен), а не для всіх, хто збігся з
        // пошуком — ще один зайвий memory grant, якого більше не існує.
        var pageArtistNames = page.Select(p => p.Name).ToList();

        var coverByArtist = (await _db.Tracks
                .AsNoTracking()
                .Where(t => pageArtistNames.Contains(t.ArtistName))
                .Select(t => new { t.ArtistName, t.PlayCount, t.IsExternal, t.ExternalCoverUrl, t.CoverImageRelativePath })
                .ToListAsync(cancellationToken))
            .GroupBy(t => t.ArtistName)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(t => t.PlayCount).First());

        var albumCounts = await _db.Albums
            .AsNoTracking()
            .Where(a => pageArtistNames.Contains(a.ArtistName))
            .GroupBy(a => a.ArtistName)
            .Select(g => new { Name = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Name, x => x.Count, cancellationToken);

        var items = page
            .Select(g =>
            {
                var cover = coverByArtist.GetValueOrDefault(g.Name);
                return new ArtistListItemDto(
                    g.Name,
                    g.TrackCount,
                    albumCounts.TryGetValue(g.Name, out var albumCount) ? albumCount : 0,
                    cover is null ? null : BuildCoverUrl(cover.IsExternal, cover.ExternalCoverUrl, cover.CoverImageRelativePath, baseUrl),
                    g.TotalPlayCount);
            })
            .ToList();

        await _cache.SetAsync(
            cacheKey, new CachedPage<ArtistListItemDto> { Items = items, TotalCount = totalCount },
            TimeSpan.FromMinutes(5), cancellationToken);

        return Ok(new PagedArtistResultDto(items, totalCount, pageNumber, pageSize));
    }

    private static string? BuildCoverUrl(bool isExternal, string? externalCoverUrl, string? coverImageRelativePath, string baseUrl)
    {
        if (isExternal) return externalCoverUrl;
        if (!string.IsNullOrWhiteSpace(coverImageRelativePath))
            return $"{baseUrl}/music/files/{coverImageRelativePath.Replace('\\', '/')}";
        return null;
    }
}
