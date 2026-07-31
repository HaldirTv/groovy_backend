using Groovra.History.Microservice.DTOS;
using Groovra.History.Microservice.Data;
using Groovra.Shared.Extensions;
using Groovra.Shared.Grpc;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Groovra.History.Microservice.Controllers;

[ApiController]
[Route("api/history")]
public class HistoryController : ControllerBase
{
    private readonly HistoryDbContext _db;
    private readonly TrackInfoGrpcService.TrackInfoGrpcServiceClient _trackInfoClient;
    private readonly ILogger<HistoryController> _logger;

    public HistoryController(HistoryDbContext db, TrackInfoGrpcService.TrackInfoGrpcServiceClient trackInfoClient, ILogger<HistoryController> logger)
    {
        _db = db;
        _trackInfoClient = trackInfoClient;
        _logger = logger;
    }

    [HttpGet]
    public async Task<IActionResult> GetUserHistory(
        [FromQuery] Guid? userId, 
        [FromQuery] int pageNumber = 1, 
        [FromQuery] int pageSize = 10, 
        CancellationToken cancellationToken = default)
    {
        if (!Request.HttpContext.TryGetUserId(out var userIdFromHeader))
            return Unauthorized(new { Message = "User ID must be provided." });

        userId ??= userIdFromHeader;

        // Дедупликация по TrackId (последнее прослушивание) и пагинация делаются на стороне
        // SQL через GROUP BY, а не в памяти над выборкой окна — иначе TotalCount врёт и
        // уникальные треки за пределами Take-окна пропадают на дальних страницах.
        var uniqueQuery = _db.PlaybackHistories
            .Where(h => h.UserId == userId.Value)
            .GroupBy(h => h.TrackId)
            .Select(g => new { TrackId = g.Key, PlayedAt = g.Max(h => h.PlayedAt) });

        var totalCount = await uniqueQuery.CountAsync(cancellationToken);

        var historyItems = await uniqueQuery
            .OrderByDescending(x => x.PlayedAt)
            .Skip((pageNumber - 1) * pageSize)
            .Take(pageSize)
            .ToListAsync(cancellationToken);

        if (!historyItems.Any())
            return Ok(new { Items = new List<UserHistoryRichResponseDto>(), TotalCount = 0 });

        var request = new TrackInfoRequest { CurrentUserId = userId.ToString() };
        request.TrackIds.AddRange(historyItems.Select(h => h.TrackId.ToString()).Distinct());

        // Track metadata is an enrichment, not the source of truth for this endpoint - if Music is
        // slow/unreachable, degrade to "Unknown" fields instead of failing the whole history request.
        var trackDict = new Dictionary<string, TrackDetails>();
        try
        {
            var grpcResponse = await _trackInfoClient.GetTracksInfoAsync(
                request,
                deadline: DateTime.UtcNow.AddSeconds(5),
                cancellationToken: cancellationToken);
            trackDict = grpcResponse.Tracks.ToDictionary(t => t.TrackId, t => t);
        }
        catch (Grpc.Core.RpcException ex)
        {
            _logger.LogWarning(ex, "GetTracksInfoAsync failed while enriching history for user {UserId}; returning history without track metadata", userId);
        }

        var richItems = historyItems.Select(h => 
        {
            trackDict.TryGetValue(h.TrackId.ToString(), out var t);
            return new UserHistoryRichResponseDto
            {
                PlayedAt = h.PlayedAt,
                TrackId = h.TrackId,
                Title = t?.Title ?? "Unknown",
                ArtistName = t?.ArtistName ?? "Unknown",
                Album = string.IsNullOrEmpty(t?.Album) ? null : t.Album,
                Genre = string.IsNullOrEmpty(t?.Genre) ? null : t.Genre,
                DurationSeconds = t?.DurationSeconds ?? 0,
                FileSizeBytes = t?.FileSizeBytes ?? 0,
                ContentType = t?.ContentType ?? "",
                AudioUrl = t?.AudioUrl ?? "",
                CoverImageUrl = string.IsNullOrEmpty(t?.CoverImageUrl) ? null : t.CoverImageUrl,
                UploadedAt = DateTime.TryParse(t?.UploadedAt, out var d) ? d : default,
                PlayCount = t?.PlayCount ?? 0,
                IsLiked = t?.IsLiked ?? false
            };
        }).ToList();
        return Ok(new 
        { 
            Items = richItems, 
            TotalCount = totalCount,
            PageNumber = pageNumber,
            PageSize = pageSize
        });
    }
}

public class UserHistoryRichResponseDto
{
    public Guid TrackId { get; set; }
    public string Title { get; set; } = string.Empty;
    public string ArtistName { get; set; } = string.Empty;
    public string? Album { get; set; }
    public string? Genre { get; set; }
    public double DurationSeconds { get; set; }
    public long FileSizeBytes { get; set; }
    public string ContentType { get; set; } = string.Empty;
    public string AudioUrl { get; set; } = string.Empty;
    public string? CoverImageUrl { get; set; }
    public DateTime UploadedAt { get; set; }
    public long PlayCount { get; set; }
    public bool IsLiked { get; set; }
    public DateTime PlayedAt { get; set; }
}