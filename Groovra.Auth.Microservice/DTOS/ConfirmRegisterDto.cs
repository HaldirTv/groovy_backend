using System.ComponentModel.DataAnnotations;

namespace Groovra.Auth.Microservice.DTOs;

public class ConfirmRegisterDto
{
    [Required] [EmailAddress] [MaxLength(256)]
    public string Email { get; set; } = string.Empty;

    [Required] [StringLength(6, MinimumLength = 6, ErrorMessage = "Код должен быть 6 символов.")]
    [RegularExpression(@"^\d{6}$", ErrorMessage = "Код должен содержать только цифры.")]
    public string Code { get; set; } = string.Empty;
    
    [StringLength(100, ErrorMessage = "DeviceId слишком длинный.")]
    public string? DeviceId { get; set; }
}