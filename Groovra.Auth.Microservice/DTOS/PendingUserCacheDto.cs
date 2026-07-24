namespace Groovra.Auth.Microservice.DTOs;

public class PendingUserCacheDto
{
    public string Username { get; set; } = string.Empty;
    public string Email { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public string ConfirmationCode { get; set; } = string.Empty;
}