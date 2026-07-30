namespace Groovra.Auth.Microservice.DTOS;

public record UserSearchResultDto(Guid UserId, string Username, string? AvatarUrl);
