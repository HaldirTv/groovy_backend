using Grpc.Core;
using Microsoft.EntityFrameworkCore;
using Groovra.Shared.Grpc;
using Groovra.Music.Microservice.Model;
using Groovra.Music.Microservice.Services;

namespace Groovra.Music.Microservice.Grpc;

public class TrackInfoGrpcServer : TrackInfoGrpcService.TrackInfoGrpcServiceBase
{
    private readonly MusicDbContext _db;
    private readonly FavoritesService _favoritesService;
    private readonly IConfiguration _configuration;

    public TrackInfoGrpcServer(MusicDbContext db, FavoritesService favoritesService, IConfiguration configuration)
    {
        _db = db;
        _favoritesService = favoritesService;
        _configuration = configuration;
    }

    public override async Task<TrackInfoResponse> GetTracksInfo(TrackInfoRequest request, ServerCallContext context)
    {
        var response = new TrackInfoResponse();
        var baseUrl = _configuration["BaseUrl"] ?? "https://localhost:7005"; 

        // 1. Парсим ID треков и ID юзера
        var guids = request.TrackIds
            .Select(id => Guid.TryParse(id, out var g) ? g : Guid.Empty)
            .Where(g => g != Guid.Empty)
            .ToList();

        _ = Guid.TryParse(request.CurrentUserId, out var userId);

        // 2. Достаем треки из БД
        var tracks = await _db.Tracks
            .AsNoTracking()
            .Where(t => guids.Contains(t.Id))
            .ToListAsync();

        // 3. Получаем лайки юзера, если он передан
        var likedIds = userId != Guid.Empty
            ? await _favoritesService.GetLikedTrackIdsAsync(userId)
            : new HashSet<Guid>();

        // 4. Маппим в gRPC-ответ (полный аналог твоего TrackDto)
        foreach (var track in tracks)
        {
            var audioUrl = $"{baseUrl}/music/tracks/{track.Id}/stream";
            
            string coverUrl = "";
            if (track.IsExternal)
                coverUrl = track.ExternalCoverUrl ?? "";
            else if (!string.IsNullOrWhiteSpace(track.CoverImageRelativePath))
                coverUrl = $"{baseUrl}/music/files/{track.CoverImageRelativePath.Replace('\\', '/')}";

            response.Tracks.Add(new TrackDetails
            {
                TrackId = track.Id.ToString(),
                Title = track.Title,
                ArtistName = track.ArtistName,
                Album = track.Album ?? "", // gRPC не любит null
                Genre = track.Genre ?? "",
                DurationSeconds = track.DurationSeconds,
                FileSizeBytes = track.FileSizeBytes,
                ContentType = track.ContentType ?? "",
                AudioUrl = audioUrl,
                CoverImageUrl = coverUrl,
                UploadedAt = track.UploadedAt.ToString("O"), // ISO 8601
                PlayCount = track.PlayCount,
                IsLiked = likedIds.Contains(track.Id)
            });
        }

        return response;
    }
}