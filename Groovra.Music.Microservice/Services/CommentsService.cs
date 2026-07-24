using Groovra.Music.Microservice.DTOs;
using Groovra.Music.Microservice.Model;
using Groovra.Shared.ServiceResult;
using Microsoft.EntityFrameworkCore;

namespace Groovra.Music.Microservice.Services;

public class CommentsService
{
    private readonly MusicDbContext _db;

    public CommentsService(MusicDbContext db)
    {
        _db = db;
    }

    public async Task<List<CommentResponseDto>> GetCommentsAsync(Guid trackId, Guid currentUserId, CancellationToken cancellationToken = default)
    {
        var comments = await _db.TrackComments
            .Where(c => c.TrackId == trackId)
            .OrderByDescending(c => c.CreatedAt)
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        HashSet<Guid> likedCommentIds = new();
        if (currentUserId != Guid.Empty && comments.Count > 0)
        {
            var commentIds = comments.Select(c => c.Id).ToList();
            likedCommentIds = (await _db.TrackCommentLikes
                .Where(cl => cl.UserId == currentUserId && commentIds.Contains(cl.CommentId))
                .Select(cl => cl.CommentId)
                .ToListAsync(cancellationToken)).ToHashSet();
        }

        return comments.Select(c => MapToDto(c, currentUserId, likedCommentIds)).ToList();
    }

    public async Task<ServiceResult<CommentResponseDto>> AddCommentAsync(
        Guid trackId, Guid userId, string authorName, string text, CancellationToken cancellationToken = default)
    {
        text = text?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(text))
            return ServiceResult<CommentResponseDto>.Fail("Текст коментаря не може бути порожнім.");
        if (text.Length > 2000)
            return ServiceResult<CommentResponseDto>.Fail("Коментар занадто довгий.");

        var trackExists = await _db.Tracks.AnyAsync(t => t.Id == trackId, cancellationToken);
        if (!trackExists)
            return ServiceResult<CommentResponseDto>.Fail("Трек не знайдено.");

        var comment = new TrackComment
        {
            TrackId = trackId,
            UserId = userId,
            AuthorName = string.IsNullOrWhiteSpace(authorName) ? "Користувач" : authorName,
            Text = text,
        };

        _db.TrackComments.Add(comment);
        await _db.SaveChangesAsync(cancellationToken);

        return ServiceResult<CommentResponseDto>.Ok(MapToDto(comment, userId, new HashSet<Guid>()));
    }

    public async Task<ServiceResult<int>> ToggleLikeAsync(Guid commentId, Guid userId, CancellationToken cancellationToken = default)
    {
        var comment = await _db.TrackComments.FirstOrDefaultAsync(c => c.Id == commentId, cancellationToken);
        if (comment is null)
            return ServiceResult<int>.Fail("Коментар не знайдено.");

        var existingLike = await _db.TrackCommentLikes
            .FirstOrDefaultAsync(cl => cl.CommentId == commentId && cl.UserId == userId, cancellationToken);

        if (existingLike is not null)
        {
            _db.TrackCommentLikes.Remove(existingLike);
            comment.LikesCount = Math.Max(0, comment.LikesCount - 1);
        }
        else
        {
            _db.TrackCommentLikes.Add(new TrackCommentLike { CommentId = commentId, UserId = userId });
            comment.LikesCount += 1;
        }

        await _db.SaveChangesAsync(cancellationToken);
        return ServiceResult<int>.Ok(comment.LikesCount);
    }

    public async Task<ServiceResult<bool>> DeleteCommentAsync(Guid commentId, Guid userId, bool isAdmin, CancellationToken cancellationToken = default)
    {
        var comment = await _db.TrackComments.FirstOrDefaultAsync(c => c.Id == commentId, cancellationToken);
        if (comment is null)
            return ServiceResult<bool>.Fail("Коментар не знайдено.");

        if (comment.UserId != userId && !isAdmin)
            return ServiceResult<bool>.Fail("Немає доступу до видалення цього коментаря.");

        comment.IsDeleted = true;
        await _db.SaveChangesAsync(cancellationToken);
        return ServiceResult<bool>.Ok(true);
    }

    private static CommentResponseDto MapToDto(TrackComment c, Guid currentUserId, HashSet<Guid> likedCommentIds) =>
        new(
            c.Id,
            c.TrackId,
            c.AuthorName,
            c.Text,
            c.LikesCount,
            likedCommentIds.Contains(c.Id),
            currentUserId != Guid.Empty && c.UserId == currentUserId,
            c.CreatedAt
        );
}
