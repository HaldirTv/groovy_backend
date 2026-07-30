using Groovra.ChatService.Microservice.Data;
using Groovra.Shared.Extensions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace Groovra.ChatService.Microservice.Hubs;

public class ChatHub : Hub
{
    private readonly ChatDbContext _db;

    public ChatHub(ChatDbContext db)
    {
        _db = db;
    }

    public async Task JoinConversation(Guid conversationId)
    {
        var httpContext = Context.GetHttpContext();
        if (httpContext == null || !httpContext.TryGetUserId(out var userId))
            throw new HubException("Потрібна авторизація.");

        var isParticipant = await _db.Participants
            .AnyAsync(p => p.ConversationId == conversationId && p.UserId == userId);

        if (!isParticipant)
            throw new HubException("Ви не є учасником цієї бесіди.");

        await Groups.AddToGroupAsync(Context.ConnectionId, conversationId.ToString());
    }

    public async Task LeaveConversation(Guid conversationId)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, conversationId.ToString());
    }
}
