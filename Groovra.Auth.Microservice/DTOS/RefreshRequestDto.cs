namespace Groovra.Auth.Microservice.DTOs;

public class RefreshRequestDto
{
    public string Email { get; set; } = string.Empty;
    public string DeviceId { get; set;} = string.Empty; 
}