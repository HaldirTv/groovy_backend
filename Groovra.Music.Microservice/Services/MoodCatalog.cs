namespace Groovra.Music.Microservice.Services;

// Базовий (без ML) каталог категорій настрою/стилю для секції рекомендацій на головній.
// Для кожної категорії — запасний перелік жанрів-ключових слів, якими підбираються треки,
// поки жоден трек ще не протегований полем Track.Mood вручну при завантаженні.
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
