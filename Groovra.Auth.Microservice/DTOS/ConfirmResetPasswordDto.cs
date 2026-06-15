using System.ComponentModel.DataAnnotations;

namespace Groovra.Auth.Microservice.DTOs;

public class ConfirmResetPasswordDto
{
    [Required] [EmailAddress] [MaxLength(256)]
    public string Email { get; set; } = string.Empty;

    [Required] [StringLength(32, MinimumLength = 32, ErrorMessage = "Невалидный токен.")]
    public string Token { get; set; } = string.Empty;

    [Required] [Length(8,64, ErrorMessage = "Пароль должен быть от 8 до 64 символов.")]
    public string NewPassword { get; set; } = string.Empty;
}