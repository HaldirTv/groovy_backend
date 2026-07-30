using Groovra.Auth.Microservice.Data;
using Groovra.Auth.Microservice.DTOS;
using Groovra.Auth.Microservice.Services;
using Groovra.Shared.Constants;
using Groovra.Shared.Extensions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Groovra.Auth.Microservice.Controllers;

[ApiController]
[Route("auth/users")]
public class UsersController : ControllerBase
{
    private readonly ReglogService _reglogService;
    private readonly AuthDbContext _db;

    public UsersController(ReglogService reglogService, AuthDbContext db)
    {
        _reglogService = reglogService;
        _db = db;
    }

    /// <summary>Поиск пользователей по (части) никнейма — для выбора собеседника в чате.
    /// Доступен любому залогиненному юзеру (Default policy на gateway), не только админу.</summary>
    [HttpGet("search")]
    public async Task<IActionResult> SearchUsers([FromQuery] string query, CancellationToken cancellationToken)
    {
        if (!HttpContext.TryGetUserId(out var currentUserId))
            return Unauthorized(new { Message = "Потрібна авторизація." });

        var trimmed = query?.Trim() ?? string.Empty;
        if (trimmed.Length == 0)
            return Ok(Array.Empty<UserSearchResultDto>());

        var results = await _db.Users
            .Where(u => u.Id != currentUserId && u.Username.Contains(trimmed))
            .OrderBy(u => u.Username)
            .Take(10)
            .Select(u => new
            {
                u.Id,
                u.Username,
                AvatarUrl = _db.Profiles
                    .Where(p => p.UserId == u.Id)
                    .Select(p => p.AvatarUrl)
                    .FirstOrDefault()
            })
            .ToListAsync(cancellationToken);

        var dto = results.Select(r =>
            new UserSearchResultDto(r.Id, r.Username, string.IsNullOrEmpty(r.AvatarUrl) ? null : r.AvatarUrl));

        return Ok(dto);
    }

    [HttpPost("{id:guid}/assign-artist-role")]
    public async Task<IActionResult> AssignArtistRole(Guid id, CancellationToken cancellationToken)
    {
        if (!HttpContext.TryGetUserId(out _))
            return Unauthorized(new { Message = "Потрібна авторизація." });

        if (!HttpContext.UserIsInRole(AppRoles.Admin))
            return StatusCode(StatusCodes.Status403Forbidden, new { Message = "Недостатньо прав." });

        var result = await _reglogService.AssignArtistRoleAsync(id, cancellationToken);
        if (!result.Success) return BadRequest(new { Message = result.ErrorMessage });

        return Ok(new { Message = "Artist role assigned successfully." });
    }
}
