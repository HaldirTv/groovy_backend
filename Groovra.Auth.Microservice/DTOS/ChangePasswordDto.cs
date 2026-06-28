using System.ComponentModel.DataAnnotations;

namespace Groovra.Auth.Microservice.DTOs;

public class ChangePasswordDto : IValidatableObject
{
    [Required(ErrorMessage = "Старый пароль обязателен.")]
    [MaxLength(128)]
    public string OldPassword { get; set; } = string.Empty;

    [Required(ErrorMessage = "Новый пароль обязателен.")]
    [Length(8, 64, ErrorMessage = "Новый пароль должен быть от 8 до 64 символов.")]
    public string NewPassword { get; set; } = string.Empty;


    public IEnumerable<ValidationResult> Validate(ValidationContext ctx)
    {
        if (OldPassword == NewPassword)
            yield return new ValidationResult(
                "Новый пароль не должен совпадать со старым.",
                new[] { nameof(NewPassword) });
    }
}