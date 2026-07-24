namespace Groovra.Music.Microservice.DTOs;

/// <summary>
/// Одна категорія настрою/стилю з підібраними треками для секції рекомендацій на головній.
/// </summary>
public class MoodRecommendationDto
{
    public string Mood { get; set; } = string.Empty;
    public List<TrackDto> Tracks { get; set; } = [];
}
