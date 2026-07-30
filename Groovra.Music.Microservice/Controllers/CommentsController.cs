using System;
using System.Threading;
using System.Threading.Tasks;
using Groovra.Music.Microservice.DTOs;
using Groovra.Music.Microservice.Services;
using Groovra.Shared.Constants;
using Groovra.Shared.Extensions;
using Microsoft.AspNetCore.Mvc;

namespace Groovra.Music.Microservice.Controllers;

[ApiController]
[Route("music/tracks")]
public class CommentsController : ControllerBase
{
    private readonly CommentsService _commentsService;

    public CommentsController(CommentsService commentsService)
    {
        _commentsService = commentsService;
    }

    [HttpGet("{trackId}/comments")]
    public async Task<IActionResult> GetComments(string trackId, CancellationToken ct)
    {
        Guid? currentUserId = HttpContext.TryGetUserId(out var userId) ? userId : null;
        var comments = await _commentsService.GetCommentsAsync(trackId, currentUserId, ct);
        return Ok(comments);
    }

    [HttpPost("{trackId}/comments")]
    public async Task<IActionResult> CreateComment(string trackId, [FromBody] CreateCommentDto dto, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(dto.Text))
        {
            return BadRequest(new { Error = "Текст коментаря не може бути порожнім" });
        }
        if (dto.Text.Length > 2000)
        {
            return BadRequest(new { Error = "Текст коментаря занадто довгий (максимум 2000 символів)" });
        }

        Guid? currentUserId = HttpContext.TryGetUserId(out var userId) ? userId : null;
        string authorName = HttpContext.GetUserName();
        if (string.IsNullOrWhiteSpace(authorName))
        {
            authorName = "Користувач";
        }

        var result = await _commentsService.AddCommentAsync(trackId, currentUserId, authorName, dto.Text, ct);
        return Ok(result);
    }

    [HttpPost("comments/{commentId:guid}/like")]
    public async Task<IActionResult> ToggleLike(Guid commentId, CancellationToken ct)
    {
        if (!HttpContext.TryGetUserId(out var userId))
        {
            return Unauthorized(new { Error = "Для того щоб ставити лайки, потрібно увійти" });
        }

        var result = await _commentsService.ToggleLikeAsync(commentId, userId, ct);
        if (!result)
        {
            return NotFound(new { Error = "Коментар не знайдено" });
        }

        return Ok(new { Success = true });
    }

    [HttpDelete("comments/{commentId:guid}")]
    public async Task<IActionResult> DeleteComment(Guid commentId, CancellationToken ct)
    {
        if (!HttpContext.TryGetUserId(out var userId))
        {
            return Unauthorized(new { Error = "Необхідно увійти" });
        }

        bool isAdmin = HttpContext.UserIsInRole(AppRoles.Admin);
        var result = await _commentsService.DeleteCommentAsync(commentId, userId, isAdmin, ct);
        if (!result)
        {
            return BadRequest(new { Error = "Не вдалося видалити коментар" });
        }

        return Ok(new { Success = true });
    }
}
