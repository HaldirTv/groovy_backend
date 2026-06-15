using System.ComponentModel.DataAnnotations;

namespace Groovra.Auth.Microservice.DTOs;

public class RegisterDto
{
    [Required(ErrorMessage = "Имя пользователя обязательно.")]
    [MinLength(3, ErrorMessage = "Имя пользователя минимум 3 символа.")]
    [MaxLength(50, ErrorMessage = "Имя пользователя максимум 50 символов.")]
    [RegularExpression(@"^[a-zA-Z0-9_.-]+$",
        ErrorMessage = "Имя пользователя может содержать только буквы, цифры, _, . и -.")]
    public string Username { get; set; } = string.Empty;

    [Required(ErrorMessage = "Email обязателен.")]
    [MaxLength(256, ErrorMessage = "Email не может быть длиннее 256 символов.")]
    [EmailAddress(ErrorMessage = "Некорректный формат email.")]
    public string Email { get; set; } = string.Empty;

    [Required(ErrorMessage = "Пароль обязателен.")]
    [Length(8, 64, ErrorMessage = "Пароль должен быть от 8 до 64 символов.")]
    public string Password { get; set; } = string.Empty;
    
    public string? Role { get; set; } = "Listener";
}