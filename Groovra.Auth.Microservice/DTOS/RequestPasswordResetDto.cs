namespace Groovra.Auth.Microservice.DTOs;
using System.ComponentModel.DataAnnotations;


public record RequestPasswordResetDto(
    [Required(ErrorMessage = "Email обязателен.")]
    [EmailAddress(ErrorMessage = "Некорректный формат email.")]
    [MaxLength(256)]
    string Email
);