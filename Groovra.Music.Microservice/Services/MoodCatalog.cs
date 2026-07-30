namespace Groovra.Music.Microservice.Services;

public static class MoodCatalog
{
    public static readonly IReadOnlyDictionary<string, string[]> FallbackGenres = new Dictionary<string, string[]>
    {
        ["Chill"] = ["Ambient", "Lo-Fi", "Jazz", "Classical", "Acoustic"],
        ["Workout"] = ["Electronic", "Hip-Hop", "Dance", "Pop"],
        ["Focus"] = ["Classical", "Ambient", "Instrumental"],
        ["Party"] = ["Pop", "Hip-Hop", "Electronic", "Dance", "House"],
        ["Sad"] = ["Blues", "Acoustic", "Classical"],
        ["Happy"] = ["Pop", "Funk", "Reggae"],
    };
}
