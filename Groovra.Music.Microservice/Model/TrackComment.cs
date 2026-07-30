using System;

namespace Groovra.Music.Microservice.Model;

public class TrackComment
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string TrackId { get; set; } = string.Empty;
    public Guid? UserId { get; set; }
    public string AuthorName { get; set; } = "Гість";
    public string Text { get; set; } = string.Empty;
    public int LikesCount { get; set; } = 0;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public bool IsDeleted { get; set; } = false;
}

public class TrackCommentLike
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid CommentId { get; set; }
    public Guid UserId { get; set; }
}
