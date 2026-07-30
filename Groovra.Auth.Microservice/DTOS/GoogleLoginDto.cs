namespace Groovra.Auth.Microservice.DTOs;

public record GoogleLoginDto(string Code, string? DeviceId, string? RedirectUri);