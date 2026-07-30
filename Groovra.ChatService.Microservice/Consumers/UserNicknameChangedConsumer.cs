using Groovra.ChatService.Microservice.Data;
using Groovra.Messaging.Contracts;
using MassTransit;
using Microsoft.EntityFrameworkCore;

namespace Groovra.ChatService.Microservice.Consumers;

// Клас навмисно названо унікально в межах усіх сервісів - MassTransit формує ім'я черги RabbitMQ
// з імені класу консьюмера, а не з назви сервісу; однакове ім'я класу в Music і Chat змусило б
// обидва сервіси конкурувати за ОДНУ чергу замість отримання кожним власної копії події.
public class ChatUserNicknameChangedConsumer : IConsumer<UserNicknameChangedEvent>
{
    private readonly ChatDbContext _db;
    private readonly ILogger<ChatUserNicknameChangedConsumer> _logger;

    public ChatUserNicknameChangedConsumer(ChatDbContext db, ILogger<ChatUserNicknameChangedConsumer> logger)
    {
        _db = db;
        _logger = logger;
    }

    public async Task Consume(ConsumeContext<UserNicknameChangedEvent> context)
    {
        var message = context.Message;
        var ct = context.CancellationToken;

        var messageRows = await _db.Messages
            .Where(m => m.SenderId == message.UserId)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.SenderName, message.NewNickname), ct);

        var participantRows = await _db.Participants
            .Where(p => p.UserId == message.UserId)
            .ExecuteUpdateAsync(s => s.SetProperty(p => p.UserName, message.NewNickname), ct);

        var ownMessageIds = await _db.Messages
            .Where(m => m.SenderId == message.UserId)
            .Select(m => m.Id)
            .ToListAsync(ct);

        var forwardedRows = 0;
        if (ownMessageIds.Count > 0)
        {
            forwardedRows = await _db.Messages
                .Where(m => m.ForwardedFromMessageId != null && ownMessageIds.Contains(m.ForwardedFromMessageId.Value))
                .ExecuteUpdateAsync(s => s.SetProperty(m => m.ForwardedFromSenderName, message.NewNickname), ct);
        }

        _logger.LogInformation(
            "[RabbitMQ] UserNicknameChangedEvent оброблено для {UserId}: {MessageRows} повідомлень, {ParticipantRows} учасників, {ForwardedRows} пересланих повідомлень оновлено на ім'я '{NewNickname}'",
            message.UserId, messageRows, participantRows, forwardedRows, message.NewNickname);
    }
}
