namespace Groovra.Music.Microservice.DTOs;

public class MoodRecommendationDto
{
    public string Mood { get; set; } = string.Empty;
    public List<TrackDto> Tracks { get; set; } = [];
}
