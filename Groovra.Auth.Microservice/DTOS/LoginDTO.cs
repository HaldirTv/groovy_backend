using System.ComponentModel.DataAnnotations;

namespace Groovra.Auth.Microservice.DTOs;

public class LoginDto
{
    [Required(ErrorMessage = "Email обязателен.")]
    [EmailAddress(ErrorMessage = "Некорректный формат email.")]
    [MaxLength(256)]
    public string Email { get; set; } = string.Empty;

    [Required(ErrorMessage = "Пароль обязателен.")]
    [MaxLength(64, ErrorMessage = "Пароль слишком длинный.")]
    public string Password { get; set; } = string.Empty;

    [StringLength(100, ErrorMessage = "DeviceId слишком длинный.")]
    public string? DeviceId { get; set; }
}