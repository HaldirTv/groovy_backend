using System.ComponentModel.DataAnnotations;

namespace Groovra.Auth.Microservice.DTOs;

public class VerifyCodeDto
{
    [Required] [EmailAddress] [MaxLength(256)]
    public string Email { get; set; } = string.Empty;

    [Required] [StringLength(6, MinimumLength = 6)]
    [RegularExpression(@"^\d{6}$", ErrorMessage = "Код должен содержать только цифры.")]
    public string Code { get; set; } = string.Empty;
}