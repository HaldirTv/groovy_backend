using System.ComponentModel.DataAnnotations;

namespace Groovra.Auth.Microservice.DTOs;

public class LogoutDto
{
    [Required(ErrorMessage = "Email обязателен.")]
    [EmailAddress(ErrorMessage = "Некорректный формат email.")]
    [MaxLength(256)]
    public string Email { get; set; } = string.Empty;

    [StringLength(128, ErrorMessage = "DeviceId слишком длинный.")]
    public string? DeviceId { get; set; }
}