using Groovra.History.Microservice.Data;
using Groovra.Messaging.Contracts;
using MassTransit;
using Microsoft.EntityFrameworkCore;

namespace Groovra.History.Microservice.Consumers;

public class TrackPlayedConsumer: IConsumer<TrackPlayedEvent>
{
    private static readonly TimeSpan DuplicateWindow = TimeSpan.FromSeconds(10);

    private readonly HistoryDbContext _db;
    private readonly ILogger<TrackPlayedConsumer> _logger;
    public TrackPlayedConsumer(HistoryDbContext db, ILogger<TrackPlayedConsumer> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task Consume(ConsumeContext<TrackPlayedEvent> context)
    {
        var message = context.Message;
        _logger.LogInformation("[RabbitMQ] Успішно піймали івент! Юзер {UserId} слухав трек {TrackId}",
            message.UserId, message.TrackId);

        var windowStart = message.PlayedAt - DuplicateWindow;
        var windowEnd = message.PlayedAt + DuplicateWindow;
        var alreadyLogged = await _db.PlaybackHistories
            .AnyAsync(h => h.UserId == message.UserId
                && h.TrackId == message.TrackId
                && h.PlayedAt >= windowStart
                && h.PlayedAt <= windowEnd, context.CancellationToken);

        if (alreadyLogged)
        {
            _logger.LogInformation("[RabbitMQ] Дублікат події в межах {Seconds}с проігноровано для юзера {UserId} та треку {TrackId}",
                DuplicateWindow.TotalSeconds, message.UserId, message.TrackId);
            return;
        }

        var historyEntry = new PlaybackHistory
        {
            Id = Guid.NewGuid(),
            UserId = message.UserId,
            TrackId = message.TrackId,
            PlayedAt = message.PlayedAt
        };
        _db.PlaybackHistories.Add(historyEntry);
        await _db.SaveChangesAsync(context.CancellationToken);
        _logger.LogInformation("[RabbitMQ] Івент оброблено і записано в базу даних для юзера {UserId} та треку {TrackId}",
            message.UserId, message.TrackId);
    }
}
