namespace Groovra.Music.Microservice.DTOs;

public record CreateCommentDto(string Text);

public record CommentResponseDto(
    Guid Id,
    Guid TrackId,
    string AuthorName,
    string Text,
    int Likes,
    bool IsLiked,
    bool IsOwn,
    DateTime CreatedAt
);
