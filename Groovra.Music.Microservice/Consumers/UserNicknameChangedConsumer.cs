using Groovra.Messaging.Contracts;
using Groovra.Music.Microservice.Caching;
using Groovra.Music.Microservice.Model;
using MassTransit;
using Microsoft.EntityFrameworkCore;

namespace Groovra.Music.Microservice.Consumers;

// Клас навмисно названо унікально в межах усіх сервісів (не просто UserNicknameChangedConsumer) -
// MassTransit за замовчуванням формує ім'я черги RabbitMQ з ІМЕНІ КЛАСУ консьюмера, а не з назви
// сервісу. Якщо Music і Chat матимуть консьюмери з однаковим іменем класу, обидва зв'яжуться з
// ОДНІЄЮ й тією ж чергою і почнуть конкурувати за повідомлення (тільки один отримає кожну подію)
// замість того, щоб кожен сервіс отримував власну копію.
public class MusicUserNicknameChangedConsumer : IConsumer<UserNicknameChangedEvent>
{
    private readonly MusicDbContext _db;
    private readonly ICacheService _cache;
    private readonly ILogger<MusicUserNicknameChangedConsumer> _logger;

    public MusicUserNicknameChangedConsumer(MusicDbContext db, ICacheService cache, ILogger<MusicUserNicknameChangedConsumer> logger)
    {
        _db = db;
        _cache = cache;
        _logger = logger;
    }

    public async Task Consume(ConsumeContext<UserNicknameChangedEvent> context)
    {
        var message = context.Message;
        var ct = context.CancellationToken;

        var affectedTrackIds = await _db.Tracks
            .Where(t => t.UserId == message.UserId)
            .Select(t => t.Id)
            .ToListAsync(ct);

        var affectedAlbumIds = await _db.Albums
            .Where(a => a.UserId == message.UserId)
            .Select(a => a.Id)
            .ToListAsync(ct);

        if (affectedTrackIds.Count == 0 && affectedAlbumIds.Count == 0)
        {
            _logger.LogInformation("[RabbitMQ] UserNicknameChangedEvent для {UserId}: немає треків/альбомів у Music для оновлення", message.UserId);
            return;
        }

        await _db.Tracks
            .Where(t => t.UserId == message.UserId)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.ArtistName, message.NewNickname), ct);

        await _db.Albums
            .Where(a => a.UserId == message.UserId)
            .ExecuteUpdateAsync(s => s.SetProperty(a => a.ArtistName, message.NewNickname), ct);

        await _db.TrackComments
            .Where(c => c.UserId == message.UserId)
            .ExecuteUpdateAsync(s => s.SetProperty(c => c.AuthorName, message.NewNickname), ct);

        foreach (var trackId in affectedTrackIds)
        {
            await _cache.RemoveAsync(CacheKeys.Track(trackId), ct);
        }

        foreach (var pattern in CacheKeys.ListPatterns)
        {
            await _cache.RemoveByPatternAsync(pattern, ct);
        }
        await _cache.RemoveByPatternAsync(CacheKeys.AlbumsSearchPatternAll, ct);

        _logger.LogInformation(
            "[RabbitMQ] UserNicknameChangedEvent оброблено для {UserId}: {TrackCount} треків, {AlbumCount} альбомів оновлено на ім'я '{NewNickname}'",
            message.UserId, affectedTrackIds.Count, affectedAlbumIds.Count, message.NewNickname);
    }
}
