using System.ComponentModel.DataAnnotations;

namespace Groovra.Music.Microservice.DTOs;

public record LikeRequestDto([Required] Guid TrackId);