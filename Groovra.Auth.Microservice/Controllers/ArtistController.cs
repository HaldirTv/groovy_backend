using Groovra.Auth.Microservice.Data;
using Groovra.Auth.Microservice.DTOS;
using Groovra.Auth.Microservice.Models;
using Groovra.Shared.Constants;
using Groovra.Shared.Extensions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Groovra.Auth.Microservice.Controllers;

/// <summary>
/// Самообслуговування для переходу зі слухача в артиста: перевірка статусу та подача заявки.
/// Саму роль Artist видає лише адмін через POST /auth/users/{id}/assign-artist-role — тут
/// заявка тільки фіксується, без автозатвердження.
/// </summary>
[ApiController]
[Route("artist")]
public class ArtistController : ControllerBase
{
    private readonly AuthDbContext _db;

    public ArtistController(AuthDbContext db)
    {
        _db = db;
    }

    [HttpGet("status")]
    public async Task<IActionResult> GetStatus(CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(Request.Headers["X-User-Id"], out Guid userId))
            return Unauthorized(new { Message = "User ID invalid or missing." });

        var isArtist = Request.Headers["X-User-Role"].ToString().HasRole(AppRoles.Artist);

        var profile = await _db.Profiles.AsNoTracking().FirstOrDefaultAsync(p => p.UserId == userId, cancellationToken);
        var artistProfile = isArtist
            ? await _db.Artists.AsNoTracking().FirstOrDefaultAsync(a => a.UserId == userId, cancellationToken)
            : null;

        return Ok(new ArtistStatusResponseDto
        {
            IsArtist = isArtist,
            ApplicationStatus = string.IsNullOrEmpty(profile?.ArtistApplicationStatus) ? "None" : profile.ArtistApplicationStatus,
            ArtistName = profile?.ArtistApplicationName,
            Bio = artistProfile?.Bio,
            SubmittedAt = profile?.ArtistApplicationSubmittedAt,
        });
    }

    [HttpPost("apply")]
    public async Task<IActionResult> Apply([FromBody] ApplyArtistDto dto, CancellationToken cancellationToken)
    {
        if (!Guid.TryParse(Request.Headers["X-User-Id"], out Guid userId))
            return Unauthorized(new { Message = "User ID invalid or missing." });

        if (Request.Headers["X-User-Role"].ToString().HasRole(AppRoles.Artist))
            return BadRequest(new { Message = "Ви вже маєте статус артиста." });

        if (string.IsNullOrWhiteSpace(dto.ArtistName))
            return BadRequest(new { Message = "Ім'я артиста обов'язкове." });

        var profile = await _db.Profiles.FirstOrDefaultAsync(p => p.UserId == userId, cancellationToken);
        if (profile is null)
        {
            profile = new Profile { UserId = userId };
            _db.Profiles.Add(profile);
        }

        if (profile.ArtistApplicationStatus == "Pending")
            return BadRequest(new { Message = "Заявку вже подано, очікуйте на розгляд." });

        profile.ArtistApplicationStatus = "Pending";
        profile.ArtistApplicationName = dto.ArtistName.Trim();
        profile.ArtistApplicationGenre = dto.Genre?.Trim() ?? string.Empty;
        profile.ArtistApplicationCountry = dto.Country?.Trim() ?? string.Empty;
        profile.ArtistApplicationPlatform = dto.Platform?.Trim() ?? string.Empty;
        profile.ArtistApplicationSubmittedAt = DateTime.UtcNow;
        profile.UpdatedAt = DateTime.UtcNow;

        await _db.SaveChangesAsync(cancellationToken);

        return Ok(new { Message = "Заявку подано. Очікуйте на розгляд адміністратором." });
    }
}
