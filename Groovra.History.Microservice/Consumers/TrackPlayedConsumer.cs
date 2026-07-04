using Groovra.History.Microservice.Data;
using Groovra.Messaging.Contracts;
using MassTransit;

namespace Groovra.History.Microservice.Consumers;

public class TrackPlayedConsumer: IConsumer<TrackPlayedEvent>
{
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
