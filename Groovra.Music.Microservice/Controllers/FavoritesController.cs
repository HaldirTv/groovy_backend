using System.Security.Claims;
using Groovra.Music.Microservice.DTOs;
using Groovra.Music.Microservice.Services;
using Microsoft.AspNetCore.Authorization;
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
        var userId = GetUserId();

        if (userId == null) 
            return Unauthorized(new { Error = "Streaming requires authentication." });

   
        var result = await _favoritesService.AddToFavoritesAsync(userId.Value, dto.TrackId);
        
        if (!result) 
            return BadRequest(new { message = "Не удалось добавить в избранное. Возможно, трека нет или он уже лайкнут." });

        return Ok(new { message = "Добавлено в избранное" });
    }

    [HttpDelete("{trackId:guid}")]
    public async Task<IActionResult> UnlikeTrack(Guid trackId)
    {
        var userId = GetUserId();
        if (userId == null) 
            return Unauthorized(new { Error = "Streaming requires authentication." });
        
        var result = await _favoritesService.RemoveFromFavoritesAsync(userId.Value, trackId);
        
        if (!result) 
            return NotFound(new { message = "Трек не найден в избранном" });

        return Ok(new { message = "Удалено из избранного" });
    }

    [HttpGet]
    public async Task<IActionResult> GetMyFavorites()
    {
        var userId = GetUserId();
        if (userId == null) 
            return Unauthorized(new { Error = "Streaming requires authentication." });

        var tracks = await _favoritesService.GetUserFavoriteTracksAsync(userId.Value);
        return Ok(tracks);
    }

    private Guid? GetUserId()
    {
        var userIdStr = Request.Headers["X-User-Id"].ToString();
    
        if (Guid.TryParse(userIdStr, out var userId))
        {
            return userId;
        }
    
        return null;
    }
}