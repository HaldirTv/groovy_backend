using Groovra.Music.Microservice.DTOs;
using Groovra.Music.Microservice.Services;
using Groovra.Shared.Constants;
using Groovra.Shared.Extensions;
using Microsoft.AspNetCore.Mvc;

namespace Groovra.Music.Microservice.Controllers;

/// <summary>
/// Комментарии к трекам. Base route: /music/tracks
/// </summary>
[ApiController]
[Route("music/tracks")]
[Produces("application/json")]
public class CommentsController : ControllerBase
{
    private readonly CommentsService _commentsService;

    public CommentsController(CommentsService commentsService)
    {
        _commentsService = commentsService;
    }

    // GET music/tracks/{trackId}/comments — публично доступно (гостям тоже)
    [HttpGet("{trackId:guid}/comments")]
    public async Task<IActionResult> GetComments(Guid trackId, CancellationToken cancellationToken)
    {
        HttpContext.TryGetUserId(out var currentUserId);
        var comments = await _commentsService.GetCommentsAsync(trackId, currentUserId, cancellationToken);
        return Ok(comments);
    }

    // POST music/tracks/{trackId}/comments
    [HttpPost("{trackId:guid}/comments")]
    public async Task<IActionResult> AddComment(Guid trackId, [FromBody] CreateCommentDto dto, CancellationToken cancellationToken)
    {
        if (!HttpContext.TryGetUserId(out var userId))
            return Unauthorized(new { message = "Потрібна авторизація." });

        var authorName = HttpContext.GetUserName();
        var result = await _commentsService.AddCommentAsync(trackId, userId, authorName, dto.Text, cancellationToken);

        if (!result.Success)
            return BadRequest(new { message = result.ErrorMessage });

        return Ok(result.Data);
    }

    // POST music/tracks/comments/{commentId}/like
    [HttpPost("comments/{commentId:guid}/like")]
    public async Task<IActionResult> ToggleLike(Guid commentId, CancellationToken cancellationToken)
    {
        if (!HttpContext.TryGetUserId(out var userId))
            return Unauthorized(new { message = "Потрібна авторизація." });

        var result = await _commentsService.ToggleLikeAsync(commentId, userId, cancellationToken);
        if (!result.Success)
            return NotFound(new { message = result.ErrorMessage });

        return Ok(new { likes = result.Data });
    }

    // DELETE music/tracks/comments/{commentId}
    [HttpDelete("comments/{commentId:guid}")]
    public async Task<IActionResult> DeleteComment(Guid commentId, CancellationToken cancellationToken)
    {
        if (!HttpContext.TryGetUserId(out var userId))
            return Unauthorized(new { message = "Потрібна авторизація." });

        var isAdmin = HttpContext.UserIsInRole(AppRoles.Admin);
        var result = await _commentsService.DeleteCommentAsync(commentId, userId, isAdmin, cancellationToken);

        if (!result.Success)
            return StatusCode(StatusCodes.Status403Forbidden, new { message = result.ErrorMessage });

        return Ok(new { message = "Коментар видалено." });
    }
}
