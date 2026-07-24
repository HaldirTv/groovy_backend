using Groovra.ChatService.Microservice.Data;
using Groovra.ChatService.Microservice.DTOS;
using Groovra.Shared.Extensions;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace Groovra.ChatService.Microservice.Controllers;

[ApiController]
[Route("chat/blocks")]
public class BlocksController : ControllerBase
{
    private readonly ChatDbContext _db;

    public BlocksController(ChatDbContext db)
    {
        _db = db;
    }

    // GET /chat/blocks — список userId, яких поточний юзер заблокував.
    [HttpGet]
    public async Task<IActionResult> GetBlockedUsers(CancellationToken cancellationToken)
    {
        if (!HttpContext.TryGetUserId(out var userId))
            return Unauthorized(new { message = "Потрібна авторизація." });

        var blockedIds = await _db.BlockedUsers
            .Where(b => b.BlockerUserId == userId)
            .Select(b => b.BlockedUserId)
            .ToListAsync(cancellationToken);

        return Ok(blockedIds);
    }

    // GET /chat/blocks/{userId}/status — чи заблокований userId мною, і чи заблокований я ним.
    [HttpGet("{userId:guid}/status")]
    public async Task<IActionResult> GetBlockStatus(Guid userId, CancellationToken cancellationToken)
    {
        if (!HttpContext.TryGetUserId(out var currentUserId))
            return Unauthorized(new { message = "Потрібна авторизація." });

        var blockedByMe = await _db.BlockedUsers
            .AnyAsync(b => b.BlockerUserId == currentUserId && b.BlockedUserId == userId, cancellationToken);
        var blockedMe = await _db.BlockedUsers
            .AnyAsync(b => b.BlockerUserId == userId && b.BlockedUserId == currentUserId, cancellationToken);

        return Ok(new BlockStatusDto(blockedByMe, blockedMe));
    }

    // POST /chat/blocks/{userId} — заблокувати користувача (ідемпотентно).
    [HttpPost("{userId:guid}")]
    public async Task<IActionResult> BlockUser(Guid userId, CancellationToken cancellationToken)
    {
        if (!HttpContext.TryGetUserId(out var currentUserId))
            return Unauthorized(new { message = "Потрібна авторизація." });

        if (userId == currentUserId)
            return BadRequest(new { message = "Не можна заблокувати самого себе." });

        var alreadyBlocked = await _db.BlockedUsers
            .AnyAsync(b => b.BlockerUserId == currentUserId && b.BlockedUserId == userId, cancellationToken);
        if (!alreadyBlocked)
        {
            _db.BlockedUsers.Add(new BlockedUser { BlockerUserId = currentUserId, BlockedUserId = userId });
            await _db.SaveChangesAsync(cancellationToken);
        }

        return NoContent();
    }

    // DELETE /chat/blocks/{userId} — розблокувати користувача.
    [HttpDelete("{userId:guid}")]
    public async Task<IActionResult> UnblockUser(Guid userId, CancellationToken cancellationToken)
    {
        if (!HttpContext.TryGetUserId(out var currentUserId))
            return Unauthorized(new { message = "Потрібна авторизація." });

        var existing = await _db.BlockedUsers
            .FirstOrDefaultAsync(b => b.BlockerUserId == currentUserId && b.BlockedUserId == userId, cancellationToken);
        if (existing != null)
        {
            _db.BlockedUsers.Remove(existing);
            await _db.SaveChangesAsync(cancellationToken);
        }

        return NoContent();
    }
}
