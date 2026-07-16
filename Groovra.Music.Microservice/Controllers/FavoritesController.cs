using Groovra.Music.Microservice.DTOs;
using Groovra.Music.Microservice.Services;
using Groovra.Shared.Extensions;
using Microsoft.AspNetCore.Mvc;

namespace Groovra.Music.Microservice.Controllers;

[ApiController]
[Route("music/favorites")]
public class FavoritesController : ControllerBase
{
    private readonly FavoritesService _favoritesService;

    public FavoritesController(FavoritesService favoritesService)
    {
        _favoritesService = favoritesService;
    }

    [HttpPost]
    public async Task<IActionResult> LikeTrack([FromBody] LikeRequestDto dto)
    {
        if (!TryGetUserId(out var userId))
            return Unauthorized(new { Error = "Streaming requires authentication." });

        var result = await _favoritesService.AddToFavoritesAsync(userId, dto.TrackId);

        if (!result)
            return BadRequest(new { message = "Не удалось добавить в избранное. Возможно, трека нет или он уже лайкнут." });

        return Ok(new { message = "Добавлено в избранное" });
    }

    [HttpDelete("{trackId:guid}")]
    public async Task<IActionResult> UnlikeTrack(Guid trackId)
    {
        if (!TryGetUserId(out var userId))
            return Unauthorized(new { Error = "Streaming requires authentication." });

        var result = await _favoritesService.RemoveFromFavoritesAsync(userId, trackId);

        if (!result)
            return NotFound(new { message = "Трек не найден в избранном" });

        return Ok(new { message = "Удалено из избранного" });
    }

    [HttpGet]
    public async Task<IActionResult> GetMyFavorites()
    {
        if (!TryGetUserId(out var userId))
            return Unauthorized(new { Error = "Streaming requires authentication." });

        var baseUrl = $"{Request.Scheme}://{Request.Host}";
        var tracks = await _favoritesService.GetUserFavoriteTracksAsync(userId, baseUrl);
        return Ok(tracks);
    }

    // ─── helpers ─────────────────────────────────────────────────────────────

    private bool TryGetUserId(out Guid userId) =>
        HttpContext.TryGetUserId(out userId);
    
}