using System.ComponentModel.DataAnnotations;

namespace Groovra.Auth.Microservice.DTOs;

public record RevokeSessionDto([StringLength(100, ErrorMessage = "DeviceId слишком длинный.")]string? DeviceId);