using System;

namespace Groovra.Music.Microservice.DTOs;

public record CreateCommentDto(string Text);

public record CommentResponseDto(
    Guid Id,
    string TrackId,
    Guid? AuthorId,
    string AuthorName,
    string Text,
    int Likes,
    bool IsLiked,
    bool IsOwn,
    DateTime CreatedAt,
    string Timestamp
);
