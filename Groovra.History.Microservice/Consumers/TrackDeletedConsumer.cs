using Groovra.History.Microservice.Data;
using Groovra.Messaging.Contracts;
using MassTransit;
using Microsoft.EntityFrameworkCore;

namespace Groovra.History.Microservice.Consumers;

public class TrackDeletedConsumer : IConsumer<TrackDeletedEvent>
{
    private readonly HistoryDbContext _db;
    private readonly ILogger<TrackDeletedConsumer> _logger;

    public TrackDeletedConsumer(HistoryDbContext db, ILogger<TrackDeletedConsumer> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task Consume(ConsumeContext<TrackDeletedEvent> context)
    {
        var message = context.Message;
        var removed = await _db.PlaybackHistories
            .Where(h => h.TrackId == message.TrackId)
            .ExecuteDeleteAsync(context.CancellationToken);

        _logger.LogInformation("[RabbitMQ] TrackDeletedEvent: видалено {Count} запис(ів) історії для треку {TrackId}",
            removed, message.TrackId);
    }
}
