using Groovra.Music.Microservice.Caching;
using Groovra.Music.Microservice.Model;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Groovra.Music.Microservice.Controllers;

public record ArtistListItemDto(
    string Name,
    int TrackCount,
    int AlbumCount,
    string? AvatarUrl,
    long TotalPlayCount
);

public record PagedArtistResultDto(
    List<ArtistListItemDto> Items,
    int TotalCount,
    int PageNumber,
    int PageSize
);

[ApiController]
[Route("music/artists")]
public class ArtistsController : ControllerBase
{
    private readonly MusicDbContext _context;
    private readonly ICacheService _cache;

    public ArtistsController(MusicDbContext context, ICacheService cache)
    {
        _context = context;
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

        var cacheKey = CacheKeys.ArtistsSearch(search, pageNumber, pageSize);
        var cached = await _cache.GetAsync<CachedPage<ArtistListItemDto>>(cacheKey, cancellationToken);
        if (cached is not null)
            return Ok(new PagedArtistResultDto(cached.Items, cached.TotalCount, pageNumber, pageSize));

        var baseUrl = $"{Request.Scheme}://{Request.Host}";

        // Группировка/агрегаты (Count/Sum) считает SQL Server через GROUP BY, пагинация —
        // тоже на стороне SQL (Skip/Take до материализации), а не в памяти после ToListAsync.
        var artistsQuery = _context.Tracks.AsNoTracking().Where(t => !t.IsDeleted);
        if (!string.IsNullOrWhiteSpace(search))
        {
            var trimmed = search.Trim();
            artistsQuery = artistsQuery.Where(t => t.ArtistName.Contains(trimmed));
        }

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

        // Обложку (трек с наибольшим PlayCount) и число альбомов тянем ТОЛЬКО для артистов
        // текущей страницы, а не для всех совпавших с поиском.
        var pageArtistNames = page.Select(p => p.Name).ToList();

        var coverByArtist = (await _context.Tracks
                .AsNoTracking()
                .Where(t => pageArtistNames.Contains(t.ArtistName))
                .Select(t => new { t.ArtistName, t.PlayCount, t.IsExternal, t.ExternalCoverUrl, t.CoverImageRelativePath })
                .ToListAsync(cancellationToken))
            .GroupBy(t => t.ArtistName)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(t => t.PlayCount).First());

        var albumCounts = await _context.Albums
            .AsNoTracking()
            .Where(a => !a.IsDeleted && pageArtistNames.Contains(a.ArtistName))
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
