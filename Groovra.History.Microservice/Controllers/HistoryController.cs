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

    public HistoryController(HistoryDbContext db, TrackInfoGrpcService.TrackInfoGrpcServiceClient trackInfoClient)
    {
        _db = db;
        _trackInfoClient = trackInfoClient;
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
    
        var query = _db.PlaybackHistories.Where(h => h.UserId == userId.Value);
        var totalCount = await query.CountAsync(cancellationToken);
        
        var historyItems = await query
            .OrderByDescending(h => h.PlayedAt)
            .Skip((pageNumber - 1) * pageSize)
            .Take(pageSize)
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        if (!historyItems.Any())
            return Ok(new { Items = new List<UserHistoryRichResponseDto>(), TotalCount = 0 });

        // 1. Формируем запрос в Music
        var request = new TrackInfoRequest { CurrentUserId = userId.ToString() };
        request.TrackIds.AddRange(historyItems.Select(h => h.TrackId.ToString()).Distinct());

        // 2. Стучимся в Music по gRPC
        var grpcResponse = await _trackInfoClient.GetTracksInfoAsync(request, cancellationToken: cancellationToken);
        
        var trackDict = grpcResponse.Tracks.ToDictionary(t => t.TrackId, t => t);

        // 3. Склеиваем историю с полными данными трека
        var richItems = historyItems.Select(h => 
        {
            trackDict.TryGetValue(h.TrackId.ToString(), out var t);
            
            return new UserHistoryRichResponseDto
            {
                // Поля из History
                PlayedAt = h.PlayedAt,
                
                // Поля из Music (сопоставляем с TrackDto)
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

// DTO ответа без зависимости от Music assembly.
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