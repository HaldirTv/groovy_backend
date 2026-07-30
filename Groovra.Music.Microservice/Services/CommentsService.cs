using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Groovra.Music.Microservice.DTOs;
using Groovra.Music.Microservice.Model;
using Microsoft.EntityFrameworkCore;

namespace Groovra.Music.Microservice.Services;

public class CommentsService
{
    private readonly MusicDbContext _db;

    public CommentsService(MusicDbContext db)
    {
        _db = db;
    }

    public async Task<List<CommentResponseDto>> GetCommentsAsync(string trackId, Guid? currentUserId, CancellationToken ct = default)
    {
        var comments = await _db.TrackComments
            .Where(c => c.TrackId == trackId)
            .OrderByDescending(c => c.CreatedAt)
            .ToListAsync(ct);

        HashSet<Guid> likedCommentIds = new();
        if (currentUserId.HasValue)
        {
            var commentIds = comments.Select(c => c.Id).ToList();
            var liked = await _db.TrackCommentLikes
                .Where(cl => cl.UserId == currentUserId.Value && commentIds.Contains(cl.CommentId))
                .Select(cl => cl.CommentId)
                .ToListAsync(ct);
            likedCommentIds = liked.ToHashSet();
        }

        return comments.Select(c => new CommentResponseDto(
            Id: c.Id,
            TrackId: c.TrackId,
            AuthorId: c.UserId,
            AuthorName: string.IsNullOrWhiteSpace(c.AuthorName) ? "Гість" : c.AuthorName,
            Text: c.Text,
            Likes: c.LikesCount,
            IsLiked: likedCommentIds.Contains(c.Id),
            IsOwn: currentUserId.HasValue && c.UserId == currentUserId.Value,
            CreatedAt: c.CreatedAt,
            Timestamp: FormatTimestamp(c.CreatedAt)
        )).ToList();
    }

    public async Task<CommentResponseDto> AddCommentAsync(string trackId, Guid? userId, string authorName, string text, CancellationToken ct = default)
    {
        var comment = new TrackComment
        {
            Id = Guid.NewGuid(),
            TrackId = trackId,
            UserId = userId,
            AuthorName = string.IsNullOrWhiteSpace(authorName) ? "Гість" : authorName,
            Text = text.Trim(),
            LikesCount = 0,
            CreatedAt = DateTime.UtcNow,
            IsDeleted = false
        };

        _db.TrackComments.Add(comment);
        await _db.SaveChangesAsync(ct);

        return new CommentResponseDto(
            Id: comment.Id,
            TrackId: comment.TrackId,
            AuthorId: comment.UserId,
            AuthorName: comment.AuthorName,
            Text: comment.Text,
            Likes: 0,
            IsLiked: false,
            IsOwn: true,
            CreatedAt: comment.CreatedAt,
            Timestamp: "Щойно"
        );
    }

    public async Task<bool> ToggleLikeAsync(Guid commentId, Guid userId, CancellationToken ct = default)
    {
        var comment = await _db.TrackComments.FirstOrDefaultAsync(c => c.Id == commentId, ct);
        if (comment == null) return false;

        var existingLike = await _db.TrackCommentLikes
            .FirstOrDefaultAsync(cl => cl.CommentId == commentId && cl.UserId == userId, ct);

        if (existingLike != null)
        {
            _db.TrackCommentLikes.Remove(existingLike);
            comment.LikesCount = Math.Max(0, comment.LikesCount - 1);
        }
        else
        {
            _db.TrackCommentLikes.Add(new TrackCommentLike
            {
                Id = Guid.NewGuid(),
                CommentId = commentId,
                UserId = userId
            });
            comment.LikesCount += 1;
        }

        await _db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> DeleteCommentAsync(Guid commentId, Guid userId, bool isAdmin, CancellationToken ct = default)
    {
        var comment = await _db.TrackComments.FirstOrDefaultAsync(c => c.Id == commentId, ct);
        if (comment == null) return false;

        if (comment.UserId != userId && !isAdmin) return false;

        comment.IsDeleted = true;
        await _db.SaveChangesAsync(ct);
        return true;
    }

    private static string FormatTimestamp(DateTime dt)
    {
        var span = DateTime.UtcNow - dt;
        if (span.TotalSeconds < 60) return "Щойно";
        if (span.TotalMinutes < 60) return $"{(int)span.TotalMinutes} хв тому";
        if (span.TotalHours < 24) return $"{(int)span.TotalHours} год тому";
        if (span.TotalDays < 7) return $"{(int)span.TotalDays} дн. тому";
        return dt.ToString("dd.MM.yyyy");
    }
}
