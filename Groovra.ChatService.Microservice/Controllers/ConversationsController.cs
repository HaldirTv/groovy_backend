using Groovra.ChatService.Microservice.Data;
using Groovra.ChatService.Microservice.DTOS;
using Groovra.ChatService.Microservice.Hubs;
using Groovra.ChatService.Microservice.Services;
using Groovra.Shared.Extensions;
using Groovra.Shared.Grpc;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;
using Microsoft.EntityFrameworkCore;

namespace Groovra.ChatService.Microservice.Controllers;

[ApiController]
[Route("chat/conversations")]
public class ConversationsController : ControllerBase
{
    private readonly ChatDbContext _db;
    private readonly TrackInfoGrpcService.TrackInfoGrpcServiceClient _trackInfoClient;
    private readonly UserNameGrpcService.UserNameGrpcServiceClient _userNameClient;
    private readonly IHubContext<ChatHub> _hub;
    private readonly IFileStorageService _fileStorage;

    public ConversationsController(
        ChatDbContext db,
        TrackInfoGrpcService.TrackInfoGrpcServiceClient trackInfoClient,
        UserNameGrpcService.UserNameGrpcServiceClient userNameClient,
        IHubContext<ChatHub> hub,
        IFileStorageService fileStorage)
    {
        _db = db;
        _trackInfoClient = trackInfoClient;
        _userNameClient = userNameClient;
        _hub = hub;
        _fileStorage = fileStorage;
    }

    [HttpGet]
    public async Task<IActionResult> GetConversations(CancellationToken cancellationToken)
    {
        if (!HttpContext.TryGetUserId(out var userId))
            return Unauthorized(new { message = "Потрібна авторизація." });

        var myParticipants = await _db.Participants
            .Where(p => p.UserId == userId)
            .ToListAsync(cancellationToken);
        var conversationIds = myParticipants.Select(p => p.ConversationId).ToList();
        var clearedAtByConversation = myParticipants.ToDictionary(p => p.ConversationId, p => p.ClearedAt);

        var conversations = await _db.Conversations
            .Where(c => conversationIds.Contains(c.Id))
            .Include(c => c.Participants)
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        var deletedMessageIds = await _db.MessageDeletions
            .Where(d => d.UserId == userId)
            .Select(d => d.MessageId)
            .ToListAsync(cancellationToken);
        var deletedMessageIdSet = deletedMessageIds.ToHashSet();

        var candidateMessages = await _db.Messages
            .Where(m => conversationIds.Contains(m.ConversationId) && !m.IsDeleted)
            .OrderByDescending(m => m.CreatedAt)
            .Select(m => new { m.Id, m.ConversationId, m.CreatedAt, m.Type, m.Text, m.MediaFileName })
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        var lastMessageByConversation = candidateMessages
            .Where(m => !deletedMessageIdSet.Contains(m.Id))
            .Where(m =>
            {
                var clearedAt = clearedAtByConversation.GetValueOrDefault(m.ConversationId);
                return clearedAt == null || m.CreatedAt > clearedAt;
            })
            .GroupBy(m => m.ConversationId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(m => m.CreatedAt).First());

        var result = conversations
            .Select(c =>
            {
                lastMessageByConversation.TryGetValue(c.Id, out var last);
                string? preview = null;
                if (last != null)
                {
                    preview = last.Type switch
                    {
                        MessageType.TrackShare => "Поділився(-лась) треком",
                        MessageType.Image => "Фото",
                        MessageType.Voice => "Голосове повідомлення",
                        MessageType.File => string.IsNullOrEmpty(last.MediaFileName) ? "Файл" : $"Файл: {last.MediaFileName}",
                        _ => last.Text
                    };
                }

                return new ConversationSummaryDto(
                    c.Id,
                    c.IsGroup,
                    c.Title,
                    c.Participants.Select(p => new ParticipantDto(p.UserId, p.UserName, p.Role.ToString())).ToList(),
                    preview,
                    last?.CreatedAt,
                    c.CreatedAt,
                    c.PinnedMessageId,
                    c.AvatarUrl,
                    c.Status.ToString(),
                    c.RequestedByUserId);
            })
            .OrderByDescending(c => c.LastMessageAt ?? c.CreatedAt)
            .ToList();

        return Ok(result);
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> GetConversation(Guid id, CancellationToken cancellationToken)
    {
        if (!HttpContext.TryGetUserId(out var userId))
            return Unauthorized(new { message = "Потрібна авторизація." });

        var conversation = await _db.Conversations
            .Include(c => c.Participants)
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == id, cancellationToken);

        if (conversation == null)
            return NotFound(new { message = "Бесіду не знайдено." });

        if (conversation.Participants.All(p => p.UserId != userId))
            return StatusCode(StatusCodes.Status403Forbidden, new { message = "Ви не є учасником цієї бесіди." });

        return Ok(await ToDtoAsync(conversation, cancellationToken));
    }

    [HttpPost]
    public async Task<IActionResult> CreateConversation(
        [FromBody] CreateConversationRequest request,
        CancellationToken cancellationToken)
    {
        if (!HttpContext.TryGetUserId(out var userId))
            return Unauthorized(new { message = "Потрібна авторизація." });

        var otherUserIds = (request.ParticipantUserIds ?? new List<Guid>())
            .Where(otherId => otherId != userId)
            .Distinct()
            .ToList();

        if (otherUserIds.Count == 0)
            return BadRequest(new { message = "Потрібен хоча б один інший учасник." });

        var isGroup = request.IsGroup || otherUserIds.Count > 1;

        if (!isGroup)
        {
            var otherId = otherUserIds[0];
            var existingId = await _db.Participants
                .Where(p => p.UserId == userId || p.UserId == otherId)
                .GroupBy(p => p.ConversationId)
                .Where(g => g.Count() == 2 && g.Any(p => p.UserId == userId) && g.Any(p => p.UserId == otherId))
                .Select(g => g.Key)
                .FirstOrDefaultAsync(cancellationToken);

            if (existingId != Guid.Empty)
            {
                var existing = await _db.Conversations
                    .Include(c => c.Participants)
                    .AsNoTracking()
                    .FirstAsync(c => c.Id == existingId, cancellationToken);

                return Ok(await ToDtoAsync(existing, cancellationToken));
            }

            var isBlocked = await _db.BlockedUsers.AnyAsync(
                b => (b.BlockerUserId == userId && b.BlockedUserId == otherId) ||
                     (b.BlockerUserId == otherId && b.BlockedUserId == userId),
                cancellationToken);
            if (isBlocked)
                return StatusCode(StatusCodes.Status403Forbidden, new { message = "Неможливо почати переписку через блокування." });
        }

        var conversation = new Conversation
        {
            IsGroup = isGroup,
            Title = isGroup ? request.Title : null,
            AvatarUrl = isGroup ? request.AvatarUrl : null,
            Status = isGroup ? ConversationStatus.Active : ConversationStatus.Pending,
            RequestedByUserId = isGroup ? null : userId
        };

        conversation.Participants.Add(new ConversationParticipant
        {
            ConversationId = conversation.Id,
            UserId = userId,
            UserName = HttpContext.GetUserName(),
            Role = isGroup ? ParticipantRole.Admin : ParticipantRole.Member
        });

        foreach (var otherId in otherUserIds)
        {
            var otherName = await ResolveUserNameAsync(otherId, cancellationToken);
            conversation.Participants.Add(new ConversationParticipant
            {
                ConversationId = conversation.Id,
                UserId = otherId,
                UserName = otherName,
                Role = ParticipantRole.Member
            });
        }

        _db.Conversations.Add(conversation);
        await _db.SaveChangesAsync(cancellationToken);

        return CreatedAtAction(nameof(GetConversation), new { id = conversation.Id }, await ToDtoAsync(conversation, cancellationToken));
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> DeleteConversation(Guid id, CancellationToken cancellationToken)
    {
        if (!HttpContext.TryGetUserId(out var userId))
            return Unauthorized(new { message = "Потрібна авторизація." });

        var conversation = await _db.Conversations
            .Include(c => c.Participants)
            .FirstOrDefaultAsync(c => c.Id == id, cancellationToken);

        if (conversation == null)
            return NotFound(new { message = "Бесіду не знайдено." });

        var membership = conversation.Participants.FirstOrDefault(p => p.UserId == userId);
        if (membership == null)
            return StatusCode(StatusCodes.Status403Forbidden, new { message = "Ви не є учасником цієї бесіди." });

        conversation.Participants.Remove(membership);
        _db.Participants.Remove(membership);

        var isLastParticipant = conversation.Participants.Count == 0;
        List<string?> mediaUrls = [];
        if (isLastParticipant)
        {
            mediaUrls = await _db.Messages
                .Where(m => m.ConversationId == id)
                .Select(m => m.MediaUrl)
                .ToListAsync(cancellationToken);
            _db.Conversations.Remove(conversation);
        }

        Guid? newAdminUserId = null;
        if (!isLastParticipant && conversation.IsGroup && membership.Role == ParticipantRole.Admin)
        {
            var newAdmin = conversation.Participants.OrderBy(p => p.JoinedAt).First();
            newAdmin.Role = ParticipantRole.Admin;
            newAdminUserId = newAdmin.UserId;
        }

        await _db.SaveChangesAsync(cancellationToken);

        if (isLastParticipant)
        {
            await _hub.Clients.Group(id.ToString()).SendAsync(
                conversation.IsGroup ? "GroupDeleted" : "ConversationDeleted",
                new { conversationId = id, byUserId = userId }, cancellationToken);
        }
        else if (conversation.IsGroup)
        {
            await _hub.Clients.Group(id.ToString()).SendAsync(
                "UserLeftGroup", new { conversationId = id, userId }, cancellationToken);

            if (newAdminUserId.HasValue)
                await _hub.Clients.Group(id.ToString()).SendAsync(
                    "GroupAdminChanged", new { conversationId = id, newAdminUserId = newAdminUserId.Value }, cancellationToken);
        }

        if (isLastParticipant)
            await DeleteMediaForMessagesAsync(mediaUrls, cancellationToken);

        return NoContent();
    }

    [HttpPost("{id:guid}/clear")]
    public async Task<IActionResult> ClearConversation(
        Guid id,
        [FromBody] ClearConversationRequest? request,
        CancellationToken cancellationToken)
    {
        if (!HttpContext.TryGetUserId(out var userId))
            return Unauthorized(new { message = "Потрібна авторизація." });

        var membership = await _db.Participants
            .FirstOrDefaultAsync(p => p.ConversationId == id && p.UserId == userId, cancellationToken);
        if (membership == null)
            return StatusCode(StatusCodes.Status403Forbidden, new { message = "Ви не є учасником цієї бесіди." });

        if (request?.ForBoth == true)
        {
            var conversation = await _db.Conversations.FirstOrDefaultAsync(c => c.Id == id, cancellationToken);
            if (conversation == null)
                return NotFound(new { message = "Бесіду не знайдено." });

            if (conversation.IsGroup && membership.Role != ParticipantRole.Admin)
                return StatusCode(StatusCodes.Status403Forbidden,
                    new { message = "Очистити історію для всіх учасників може лише адміністратор групи." });

            var messages = await _db.Messages
                .Where(m => m.ConversationId == id && !m.IsDeleted)
                .ToListAsync(cancellationToken);
            var mediaUrls = messages.Select(m => m.MediaUrl).ToList();
            foreach (var m in messages) m.IsDeleted = true;
            await _db.SaveChangesAsync(cancellationToken);

            await _hub.Clients.Group(id.ToString())
                .SendAsync("ConversationCleared", new { conversationId = id, byUserId = userId }, cancellationToken);

            await DeleteMediaForMessagesAsync(mediaUrls, cancellationToken);
        }
        else
        {
            membership.ClearedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(cancellationToken);
        }

        return NoContent();
    }

    [HttpGet("{id:guid}/messages/search")]
    public async Task<IActionResult> SearchMessages(
        Guid id,
        [FromQuery] string query,
        CancellationToken cancellationToken)
    {
        if (!HttpContext.TryGetUserId(out var userId))
            return Unauthorized(new { message = "Потрібна авторизація." });

        if (string.IsNullOrWhiteSpace(query))
            return Ok(Array.Empty<MessageDto>());

        var participant = await _db.Participants
            .FirstOrDefaultAsync(p => p.ConversationId == id && p.UserId == userId, cancellationToken);
        if (participant == null)
            return StatusCode(StatusCodes.Status403Forbidden, new { message = "Ви не є учасником цієї бесіди." });

        var clearedAt = participant.ClearedAt;

        var messages = await _db.Messages
            .Where(m => m.ConversationId == id && !m.IsDeleted)
            .Where(m => clearedAt == null || m.CreatedAt > clearedAt)
            .Where(m => !_db.MessageDeletions.Any(d => d.MessageId == m.Id && d.UserId == userId))
            .Where(m => m.Text != null && m.Text.Contains(query))
            .OrderBy(m => m.CreatedAt)
            .Take(200)
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        var replyDict = await HydrateRepliesAsync(messages, cancellationToken);
        var items = messages.Select(m => ToDto(m, new Dictionary<string, TrackDetails>(), replyDict)).ToList();
        return Ok(items);
    }

    [HttpDelete("{id:guid}/all")]
    public async Task<IActionResult> DeleteConversationForBoth(Guid id, CancellationToken cancellationToken)
    {
        if (!HttpContext.TryGetUserId(out var userId))
            return Unauthorized(new { message = "Потрібна авторизація." });

        var conversation = await _db.Conversations
            .Include(c => c.Participants)
            .FirstOrDefaultAsync(c => c.Id == id, cancellationToken);
        if (conversation == null)
            return NotFound(new { message = "Бесіду не знайдено." });

        if (conversation.Participants.All(p => p.UserId != userId))
            return StatusCode(StatusCodes.Status403Forbidden, new { message = "Ви не є учасником цієї бесіди." });

        if (conversation.IsGroup)
        {
            var caller = conversation.Participants.First(p => p.UserId == userId);
            if (caller.Role != ParticipantRole.Admin)
                return StatusCode(StatusCodes.Status403Forbidden,
                    new { message = "Видалити групу повністю може лише адміністратор." });
        }

        await DeleteConversationForBothInternalAsync(
            conversation, userId, conversation.IsGroup ? "GroupDeleted" : "ConversationDeletedForBoth", cancellationToken);

        return NoContent();
    }

    private async Task DeleteConversationForBothInternalAsync(Conversation conversation, Guid userId, string eventName, CancellationToken cancellationToken)
    {
        var id = conversation.Id;

        var mediaUrls = await _db.Messages
            .Where(m => m.ConversationId == id)
            .Select(m => m.MediaUrl)
            .ToListAsync(cancellationToken);

        _db.Conversations.Remove(conversation);
        await _db.SaveChangesAsync(cancellationToken);

        await _hub.Clients.Group(id.ToString())
            .SendAsync(eventName, new { conversationId = id, byUserId = userId }, cancellationToken);

        await DeleteMediaForMessagesAsync(mediaUrls, cancellationToken);
    }

    [HttpPost("{id:guid}/accept")]
    public async Task<IActionResult> AcceptConversationRequest(Guid id, CancellationToken cancellationToken)
    {
        if (!HttpContext.TryGetUserId(out var userId))
            return Unauthorized(new { message = "Потрібна авторизація." });

        var conversation = await _db.Conversations
            .Include(c => c.Participants)
            .FirstOrDefaultAsync(c => c.Id == id, cancellationToken);
        if (conversation == null)
            return NotFound(new { message = "Бесіду не знайдено." });

        if (conversation.Participants.All(p => p.UserId != userId))
            return StatusCode(StatusCodes.Status403Forbidden, new { message = "Ви не є учасником цієї бесіди." });

        if (conversation.Status != ConversationStatus.Pending)
            return BadRequest(new { message = "Цей запит уже оброблено." });

        if (conversation.RequestedByUserId == userId)
            return StatusCode(StatusCodes.Status403Forbidden, new { message = "Ініціатор запиту не може прийняти власний запит." });

        conversation.Status = ConversationStatus.Active;
        await _db.SaveChangesAsync(cancellationToken);

        await _hub.Clients.Group(id.ToString())
            .SendAsync("ConversationRequestAccepted", new { conversationId = id, byUserId = userId }, cancellationToken);

        return NoContent();
    }

    [HttpPost("{id:guid}/decline")]
    public async Task<IActionResult> DeclineConversationRequest(Guid id, CancellationToken cancellationToken)
    {
        if (!HttpContext.TryGetUserId(out var userId))
            return Unauthorized(new { message = "Потрібна авторизація." });

        var conversation = await _db.Conversations
            .Include(c => c.Participants)
            .FirstOrDefaultAsync(c => c.Id == id, cancellationToken);
        if (conversation == null)
            return NotFound(new { message = "Бесіду не знайдено." });

        if (conversation.Participants.All(p => p.UserId != userId))
            return StatusCode(StatusCodes.Status403Forbidden, new { message = "Ви не є учасником цієї бесіди." });

        if (conversation.Status != ConversationStatus.Pending)
            return BadRequest(new { message = "Цей запит уже оброблено." });

        if (conversation.RequestedByUserId == userId)
            return StatusCode(StatusCodes.Status403Forbidden, new { message = "Ініціатор запиту не може відхилити власний запит." });

        if (conversation.RequestedByUserId.HasValue)
        {
            var alreadyBlocked = await _db.BlockedUsers.AnyAsync(
                b => b.BlockerUserId == userId && b.BlockedUserId == conversation.RequestedByUserId.Value,
                cancellationToken);
            if (!alreadyBlocked)
                _db.BlockedUsers.Add(new BlockedUser { BlockerUserId = userId, BlockedUserId = conversation.RequestedByUserId.Value });
        }

        await DeleteConversationForBothInternalAsync(conversation, userId, "ConversationDeletedForBoth", cancellationToken);

        return NoContent();
    }

    [HttpPatch("{id:guid}/group")]
    public async Task<IActionResult> UpdateGroupInfo(
        Guid id, [FromBody] UpdateGroupInfoRequest request, CancellationToken cancellationToken)
    {
        if (!HttpContext.TryGetUserId(out var userId))
            return Unauthorized(new { message = "Потрібна авторизація." });

        var conversation = await _db.Conversations
            .Include(c => c.Participants)
            .FirstOrDefaultAsync(c => c.Id == id, cancellationToken);
        if (conversation == null)
            return NotFound(new { message = "Бесіду не знайдено." });

        if (!conversation.IsGroup)
            return BadRequest(new { message = "Редагувати назву й аватарку можна лише в груповій бесіді." });

        var caller = conversation.Participants.FirstOrDefault(p => p.UserId == userId);
        if (caller == null)
            return StatusCode(StatusCodes.Status403Forbidden, new { message = "Ви не є учасником цієї бесіди." });

        if (caller.Role != ParticipantRole.Admin)
            return StatusCode(StatusCodes.Status403Forbidden, new { message = "Редагувати групу може лише адміністратор." });

        if (request.Title != null && string.IsNullOrWhiteSpace(request.Title))
            return BadRequest(new { message = "Назва групи не може бути порожньою." });

        var oldAvatarUrl = conversation.AvatarUrl;
        var avatarChanged = request.AvatarUrl != null && request.AvatarUrl != oldAvatarUrl;

        if (request.Title != null)
            conversation.Title = request.Title.Trim();
        if (request.AvatarUrl != null)
            conversation.AvatarUrl = request.AvatarUrl;

        await _db.SaveChangesAsync(cancellationToken);

        await _hub.Clients.Group(id.ToString()).SendAsync(
            "GroupInfoUpdated",
            new { conversationId = id, title = conversation.Title, avatarUrl = conversation.AvatarUrl, byUserId = userId },
            cancellationToken);

        if (avatarChanged && !string.IsNullOrEmpty(oldAvatarUrl))
            await _fileStorage.DeleteFileAsync(oldAvatarUrl, cancellationToken);

        return Ok(await ToDtoAsync(conversation, cancellationToken));
    }

    [HttpPost("{id:guid}/participants")]
    public async Task<IActionResult> AddParticipants(
        Guid id, [FromBody] AddParticipantsRequest request, CancellationToken cancellationToken)
    {
        if (!HttpContext.TryGetUserId(out var userId))
            return Unauthorized(new { message = "Потрібна авторизація." });

        var conversation = await _db.Conversations
            .Include(c => c.Participants)
            .FirstOrDefaultAsync(c => c.Id == id, cancellationToken);
        if (conversation == null)
            return NotFound(new { message = "Бесіду не знайдено." });

        if (!conversation.IsGroup)
            return BadRequest(new { message = "Додавати учасників можна лише в групову бесіду." });

        if (conversation.Participants.All(p => p.UserId != userId))
            return StatusCode(StatusCodes.Status403Forbidden, new { message = "Ви не є учасником цієї бесіди." });

        var existingUserIds = conversation.Participants.Select(p => p.UserId).ToHashSet();
        var newUserIds = (request.UserIds ?? new List<Guid>())
            .Where(uid => !existingUserIds.Contains(uid))
            .Distinct()
            .ToList();

        var added = new List<ParticipantDto>();
        foreach (var newUserId in newUserIds)
        {
            var name = await ResolveUserNameAsync(newUserId, cancellationToken);
            var participant = new ConversationParticipant
            {
                ConversationId = id,
                UserId = newUserId,
                UserName = name,
                Role = ParticipantRole.Member
            };
            _db.Participants.Add(participant);
            added.Add(new ParticipantDto(newUserId, name, ParticipantRole.Member.ToString()));
        }

        if (added.Count > 0)
        {
            await _db.SaveChangesAsync(cancellationToken);
            await _hub.Clients.Group(id.ToString())
                .SendAsync("ParticipantsAdded", new { conversationId = id, participants = added }, cancellationToken);
        }

        return Ok(added);
    }

    [HttpDelete("{id:guid}/participants/{userId:guid}")]
    public async Task<IActionResult> RemoveParticipant(Guid id, Guid userId, CancellationToken cancellationToken)
    {
        if (!HttpContext.TryGetUserId(out var callerId))
            return Unauthorized(new { message = "Потрібна авторизація." });

        var conversation = await _db.Conversations
            .Include(c => c.Participants)
            .FirstOrDefaultAsync(c => c.Id == id, cancellationToken);
        if (conversation == null)
            return NotFound(new { message = "Бесіду не знайдено." });

        if (!conversation.IsGroup)
            return BadRequest(new { message = "Виключати учасників можна лише з групової бесіди." });

        var caller = conversation.Participants.FirstOrDefault(p => p.UserId == callerId);
        if (caller == null)
            return StatusCode(StatusCodes.Status403Forbidden, new { message = "Ви не є учасником цієї бесіди." });

        if (caller.Role != ParticipantRole.Admin)
            return StatusCode(StatusCodes.Status403Forbidden, new { message = "Виключати учасників може лише адміністратор групи." });

        if (userId == callerId)
            return BadRequest(new { message = "Щоб покинути групу, скористайтесь виходом із бесіди." });

        var target = conversation.Participants.FirstOrDefault(p => p.UserId == userId);
        if (target == null)
            return NotFound(new { message = "Учасника не знайдено в цій бесіді." });

        _db.Participants.Remove(target);
        await _db.SaveChangesAsync(cancellationToken);

        await _hub.Clients.Group(id.ToString())
            .SendAsync("ParticipantRemoved", new { conversationId = id, userId, byUserId = callerId }, cancellationToken);

        return NoContent();
    }

    [HttpGet("{id:guid}/messages")]
    public async Task<IActionResult> GetMessages(
        Guid id,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 30,
        CancellationToken cancellationToken = default)
    {
        if (!HttpContext.TryGetUserId(out var userId))
            return Unauthorized(new { message = "Потрібна авторизація." });

        var participant = await _db.Participants
            .FirstOrDefaultAsync(p => p.ConversationId == id && p.UserId == userId, cancellationToken);
        if (participant == null)
            return StatusCode(StatusCodes.Status403Forbidden, new { message = "Ви не є учасником цієї бесіди." });

        var clearedAt = participant.ClearedAt;

        var baseQuery = _db.Messages
            .Where(m => m.ConversationId == id && !m.IsDeleted)
            .Where(m => clearedAt == null || m.CreatedAt > clearedAt)
            .Where(m => !_db.MessageDeletions.Any(d => d.MessageId == m.Id && d.UserId == userId));

        var totalCount = await baseQuery.CountAsync(cancellationToken);

        var messages = await baseQuery
            .OrderByDescending(m => m.CreatedAt)
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        var trackDict = await HydrateTracksAsync(messages, userId, cancellationToken);
        var replyDict = await HydrateRepliesAsync(messages, cancellationToken);

        var items = messages
            .OrderBy(m => m.CreatedAt)
            .Select(m => ToDto(m, trackDict, replyDict))
            .ToList();

        return Ok(new { items, totalCount, page, pageSize });
    }

    [HttpPost("{id:guid}/messages")]
    public async Task<IActionResult> SendMessage(
        Guid id,
        [FromBody] SendMessageRequest request,
        CancellationToken cancellationToken)
    {
        if (!HttpContext.TryGetUserId(out var userId))
            return Unauthorized(new { message = "Потрібна авторизація." });

        if (string.IsNullOrWhiteSpace(request.Text))
            return BadRequest(new { message = "Повідомлення не може бути порожнім." });

        var isParticipant = await _db.Participants
            .AnyAsync(p => p.ConversationId == id && p.UserId == userId, cancellationToken);
        if (!isParticipant)
            return StatusCode(StatusCodes.Status403Forbidden, new { message = "Ви не є учасником цієї бесіди." });

        if (await IsBlockedInConversationAsync(id, userId, cancellationToken))
            return StatusCode(StatusCodes.Status403Forbidden, new { message = "Надсилання повідомлень у цій бесіді недоступне через блокування." });

        if (await IsPendingAndNotRequesterAsync(id, userId, cancellationToken))
            return StatusCode(StatusCodes.Status403Forbidden, new { message = "Спочатку потрібно прийняти запит на переписку." });

        var (replyValid, forwardSenderName, forwardError) = await ValidateReplyAndForwardAsync(
            id, userId, request.ReplyToMessageId, request.ForwardedFromMessageId, cancellationToken);
        if (forwardError != null)
            return forwardError;

        var message = new Message
        {
            ConversationId = id,
            SenderId = userId,
            SenderName = HttpContext.GetUserName(),
            Type = MessageType.Text,
            Text = request.Text.Trim(),
            ReplyToMessageId = replyValid ? request.ReplyToMessageId : null,
            ForwardedFromMessageId = request.ForwardedFromMessageId,
            ForwardedFromSenderName = forwardSenderName
        };

        _db.Messages.Add(message);
        await _db.SaveChangesAsync(cancellationToken);

        var replyDict = await HydrateRepliesAsync([message], cancellationToken);
        var dto = ToDto(message, new Dictionary<string, TrackDetails>(), replyDict);
        await _hub.Clients.Group(id.ToString()).SendAsync("ReceiveMessage", dto, cancellationToken);

        return Ok(dto);
    }

    [HttpPost("{id:guid}/messages/track")]
    public async Task<IActionResult> ShareTrack(
        Guid id,
        [FromBody] ShareTrackRequest request,
        CancellationToken cancellationToken)
    {
        if (!HttpContext.TryGetUserId(out var userId))
            return Unauthorized(new { message = "Потрібна авторизація." });

        var isParticipant = await _db.Participants
            .AnyAsync(p => p.ConversationId == id && p.UserId == userId, cancellationToken);
        if (!isParticipant)
            return StatusCode(StatusCodes.Status403Forbidden, new { message = "Ви не є учасником цієї бесіди." });

        if (await IsBlockedInConversationAsync(id, userId, cancellationToken))
            return StatusCode(StatusCodes.Status403Forbidden, new { message = "Надсилання повідомлень у цій бесіді недоступне через блокування." });

        if (await IsPendingAndNotRequesterAsync(id, userId, cancellationToken))
            return StatusCode(StatusCodes.Status403Forbidden, new { message = "Спочатку потрібно прийняти запит на переписку." });

        var grpcRequest = new TrackInfoRequest { CurrentUserId = userId.ToString() };
        grpcRequest.TrackIds.Add(request.TrackId.ToString());
        var grpcResponse = await _trackInfoClient.GetTracksInfoAsync(grpcRequest, cancellationToken: cancellationToken);
        var track = grpcResponse.Tracks.FirstOrDefault();

        if (track == null)
            return NotFound(new { message = "Трек не знайдено." });

        var (replyValid, forwardSenderName, forwardError) = await ValidateReplyAndForwardAsync(
            id, userId, request.ReplyToMessageId, request.ForwardedFromMessageId, cancellationToken);
        if (forwardError != null)
            return forwardError;

        var message = new Message
        {
            ConversationId = id,
            SenderId = userId,
            SenderName = HttpContext.GetUserName(),
            Type = MessageType.TrackShare,
            SharedTrackId = request.TrackId,
            ReplyToMessageId = replyValid ? request.ReplyToMessageId : null,
            ForwardedFromMessageId = request.ForwardedFromMessageId,
            ForwardedFromSenderName = forwardSenderName
        };

        _db.Messages.Add(message);
        await _db.SaveChangesAsync(cancellationToken);

        var trackDict = new Dictionary<string, TrackDetails> { [track.TrackId] = track };
        var replyDict = await HydrateRepliesAsync([message], cancellationToken);
        var dto = ToDto(message, trackDict, replyDict);
        await _hub.Clients.Group(id.ToString()).SendAsync("ReceiveMessage", dto, cancellationToken);

        return Ok(dto);
    }

    [HttpPost("{id:guid}/messages/media")]
    public async Task<IActionResult> SendMediaMessage(
        Guid id,
        [FromBody] SendMediaMessageRequest request,
        CancellationToken cancellationToken)
    {
        if (!HttpContext.TryGetUserId(out var userId))
            return Unauthorized(new { message = "Потрібна авторизація." });

        if (string.IsNullOrWhiteSpace(request.MediaUrl))
            return BadRequest(new { message = "Не вказано URL медіа." });

        if (!Enum.TryParse<MessageType>(request.MediaType, ignoreCase: true, out var mediaType) ||
            mediaType is not (MessageType.Image or MessageType.Voice or MessageType.File))
            return BadRequest(new { message = "Некоректний тип медіа. Очікується Image, Voice або File." });

        var isParticipant = await _db.Participants
            .AnyAsync(p => p.ConversationId == id && p.UserId == userId, cancellationToken);
        if (!isParticipant)
            return StatusCode(StatusCodes.Status403Forbidden, new { message = "Ви не є учасником цієї бесіди." });

        if (await IsBlockedInConversationAsync(id, userId, cancellationToken))
            return StatusCode(StatusCodes.Status403Forbidden, new { message = "Надсилання повідомлень у цій бесіді недоступне через блокування." });

        if (await IsPendingAndNotRequesterAsync(id, userId, cancellationToken))
            return StatusCode(StatusCodes.Status403Forbidden, new { message = "Спочатку потрібно прийняти запит на переписку." });

        var (replyValid, forwardSenderName, forwardError) = await ValidateReplyAndForwardAsync(
            id, userId, request.ReplyToMessageId, request.ForwardedFromMessageId, cancellationToken);
        if (forwardError != null)
            return forwardError;

        var message = new Message
        {
            ConversationId = id,
            SenderId = userId,
            SenderName = HttpContext.GetUserName(),
            Type = mediaType,
            MediaUrl = request.MediaUrl,
            MediaFileName = request.FileName,
            MediaFileSizeBytes = request.FileSizeBytes,
            ReplyToMessageId = replyValid ? request.ReplyToMessageId : null,
            ForwardedFromMessageId = request.ForwardedFromMessageId,
            ForwardedFromSenderName = forwardSenderName
        };

        _db.Messages.Add(message);
        await _db.SaveChangesAsync(cancellationToken);

        var replyDict = await HydrateRepliesAsync([message], cancellationToken);
        var dto = ToDto(message, new Dictionary<string, TrackDetails>(), replyDict);
        await _hub.Clients.Group(id.ToString()).SendAsync("ReceiveMessage", dto, cancellationToken);

        return Ok(dto);
    }

    [HttpPut("{id:guid}/messages/{messageId:guid}")]
    public async Task<IActionResult> EditMessage(
        Guid id,
        Guid messageId,
        [FromBody] EditMessageRequest request,
        CancellationToken cancellationToken)
    {
        if (!HttpContext.TryGetUserId(out var userId))
            return Unauthorized(new { message = "Потрібна авторизація." });

        if (string.IsNullOrWhiteSpace(request.Text))
            return BadRequest(new { message = "Повідомлення не може бути порожнім." });

        var isParticipant = await _db.Participants
            .AnyAsync(p => p.ConversationId == id && p.UserId == userId, cancellationToken);
        if (!isParticipant)
            return StatusCode(StatusCodes.Status403Forbidden, new { message = "Ви не є учасником цієї бесіди." });

        var message = await _db.Messages
            .FirstOrDefaultAsync(m => m.Id == messageId && m.ConversationId == id && !m.IsDeleted, cancellationToken);
        if (message == null)
            return NotFound(new { message = "Повідомлення не знайдено." });

        if (message.Type != MessageType.Text)
            return BadRequest(new { message = "Можна редагувати лише текстові повідомлення." });

        if (message.SenderId != userId)
            return StatusCode(StatusCodes.Status403Forbidden, new { message = "Редагувати можна лише власні повідомлення." });

        message.Text = request.Text.Trim();
        message.IsEdited = true;
        message.EditedAt = DateTime.UtcNow;
        await _db.SaveChangesAsync(cancellationToken);

        var replyDict = await HydrateRepliesAsync([message], cancellationToken);
        var dto = ToDto(message, new Dictionary<string, TrackDetails>(), replyDict);
        await _hub.Clients.Group(id.ToString()).SendAsync(
            "MessageEdited",
            new { conversationId = id, messageId = message.Id, text = message.Text, editedAt = message.EditedAt },
            cancellationToken);

        return Ok(dto);
    }

    [HttpPost("{id:guid}/messages/{messageId:guid}/delete")]
    public async Task<IActionResult> DeleteMessage(
        Guid id,
        Guid messageId,
        [FromBody] DeleteMessageRequest request,
        CancellationToken cancellationToken)
    {
        if (!HttpContext.TryGetUserId(out var userId))
            return Unauthorized(new { message = "Потрібна авторизація." });

        var isParticipant = await _db.Participants
            .AnyAsync(p => p.ConversationId == id && p.UserId == userId, cancellationToken);
        if (!isParticipant)
            return StatusCode(StatusCodes.Status403Forbidden, new { message = "Ви не є учасником цієї бесіди." });

        var message = await _db.Messages
            .FirstOrDefaultAsync(m => m.Id == messageId && m.ConversationId == id && !m.IsDeleted, cancellationToken);
        if (message == null)
            return NotFound(new { message = "Повідомлення не знайдено." });

        if (request.ForEveryone)
        {
            if (message.SenderId != userId)
                return StatusCode(StatusCodes.Status403Forbidden, new { message = "Видалити для всіх можна лише власне повідомлення." });

            message.IsDeleted = true;
            var mediaUrl = message.MediaUrl;

            var conversation = await _db.Conversations.FirstOrDefaultAsync(c => c.Id == id, cancellationToken);
            var wasPinned = conversation != null && conversation.PinnedMessageId == messageId;
            if (wasPinned)
                conversation!.PinnedMessageId = null;

            await _db.SaveChangesAsync(cancellationToken);

            await _hub.Clients.Group(id.ToString()).SendAsync(
                "MessageDeleted",
                new { conversationId = id, messageId = message.Id, byUserId = userId },
                cancellationToken);

            if (wasPinned)
                await _hub.Clients.Group(id.ToString()).SendAsync(
                    "MessageUnpinned", new { conversationId = id, byUserId = userId }, cancellationToken);

            if (!string.IsNullOrEmpty(mediaUrl))
            {
                var stillReferenced = await _db.Messages
                    .AnyAsync(m => m.MediaUrl == mediaUrl && !m.IsDeleted, cancellationToken);
                if (!stillReferenced)
                    await _fileStorage.DeleteFileAsync(mediaUrl, cancellationToken);
            }
        }
        else
        {
            var alreadyDeleted = await _db.MessageDeletions
                .AnyAsync(d => d.MessageId == messageId && d.UserId == userId, cancellationToken);
            if (!alreadyDeleted)
            {
                _db.MessageDeletions.Add(new MessageDeletion { MessageId = messageId, UserId = userId });
                await _db.SaveChangesAsync(cancellationToken);
            }
        }

        return NoContent();
    }

    [HttpPost("{id:guid}/messages/{messageId:guid}/pin")]
    public async Task<IActionResult> PinMessage(Guid id, Guid messageId, CancellationToken cancellationToken)
    {
        if (!HttpContext.TryGetUserId(out var userId))
            return Unauthorized(new { message = "Потрібна авторизація." });

        var conversation = await _db.Conversations
            .Include(c => c.Participants)
            .FirstOrDefaultAsync(c => c.Id == id, cancellationToken);
        if (conversation == null)
            return NotFound(new { message = "Бесіду не знайдено." });

        if (conversation.Participants.All(p => p.UserId != userId))
            return StatusCode(StatusCodes.Status403Forbidden, new { message = "Ви не є учасником цієї бесіди." });

        var message = await _db.Messages
            .FirstOrDefaultAsync(m => m.Id == messageId && m.ConversationId == id && !m.IsDeleted, cancellationToken);
        if (message == null)
            return NotFound(new { message = "Повідомлення не знайдено." });

        conversation.PinnedMessageId = messageId;
        await _db.SaveChangesAsync(cancellationToken);

        await _hub.Clients.Group(id.ToString()).SendAsync(
            "MessagePinned",
            new { conversationId = id, messageId, pinnedByUserId = userId },
            cancellationToken);

        return NoContent();
    }

    [HttpDelete("{id:guid}/pin")]
    public async Task<IActionResult> UnpinMessage(Guid id, CancellationToken cancellationToken)
    {
        if (!HttpContext.TryGetUserId(out var userId))
            return Unauthorized(new { message = "Потрібна авторизація." });

        var conversation = await _db.Conversations
            .Include(c => c.Participants)
            .FirstOrDefaultAsync(c => c.Id == id, cancellationToken);
        if (conversation == null)
            return NotFound(new { message = "Бесіду не знайдено." });

        if (conversation.Participants.All(p => p.UserId != userId))
            return StatusCode(StatusCodes.Status403Forbidden, new { message = "Ви не є учасником цієї бесіди." });

        conversation.PinnedMessageId = null;
        await _db.SaveChangesAsync(cancellationToken);

        await _hub.Clients.Group(id.ToString()).SendAsync(
            "MessageUnpinned", new { conversationId = id, byUserId = userId }, cancellationToken);

        return NoContent();
    }

    private async Task<bool> IsBlockedInConversationAsync(Guid conversationId, Guid userId, CancellationToken cancellationToken)
    {
        var conversation = await _db.Conversations
            .Include(c => c.Participants)
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == conversationId, cancellationToken);
        if (conversation == null || conversation.IsGroup)
            return false;

        var otherId = conversation.Participants.FirstOrDefault(p => p.UserId != userId)?.UserId;
        if (otherId == null)
            return false;

        return await _db.BlockedUsers.AnyAsync(
            b => (b.BlockerUserId == userId && b.BlockedUserId == otherId) ||
                 (b.BlockerUserId == otherId && b.BlockedUserId == userId),
            cancellationToken);
    }

    private async Task<bool> IsPendingAndNotRequesterAsync(Guid conversationId, Guid userId, CancellationToken cancellationToken)
    {
        var conversation = await _db.Conversations
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == conversationId, cancellationToken);

        return conversation != null
            && conversation.Status == ConversationStatus.Pending
            && conversation.RequestedByUserId != userId;
    }

    private async Task DeleteMediaForMessagesAsync(IEnumerable<string?> mediaUrls, CancellationToken cancellationToken)
    {
        foreach (var mediaUrl in mediaUrls.Distinct())
        {
            if (string.IsNullOrEmpty(mediaUrl))
                continue;

            var stillReferenced = await _db.Messages
                .AnyAsync(m => m.MediaUrl == mediaUrl && !m.IsDeleted, cancellationToken);
            if (!stillReferenced)
                await _fileStorage.DeleteFileAsync(mediaUrl, cancellationToken);
        }
    }

    private async Task<string> ResolveUserNameAsync(Guid userId, CancellationToken cancellationToken)
    {
        try
        {
            var response = await _userNameClient.GetUserNameGrpcAsync(
                new UserNameGrpcRequest { UserId = userId.ToString() },
                cancellationToken: cancellationToken);
            return response.Username;
        }
        catch (Grpc.Core.RpcException)
        {
            return string.Empty;
        }
    }

    private async Task<(bool replyValid, string? forwardSenderName, IActionResult? error)> ValidateReplyAndForwardAsync(
        Guid conversationId, Guid userId, Guid? replyToMessageId, Guid? forwardedFromMessageId, CancellationToken cancellationToken)
    {
        var replyValid = false;
        if (replyToMessageId.HasValue)
        {
            replyValid = await _db.Messages.AnyAsync(
                m => m.Id == replyToMessageId.Value && m.ConversationId == conversationId && !m.IsDeleted,
                cancellationToken);
        }

        string? forwardSenderName = null;
        if (forwardedFromMessageId.HasValue)
        {
            var source = await _db.Messages
                .FirstOrDefaultAsync(m => m.Id == forwardedFromMessageId.Value, cancellationToken);
            if (source == null)
                return (replyValid, null, NotFound(new { message = "Повідомлення для пересилання не знайдено." }));

            var wasParticipant = await _db.Participants.AnyAsync(
                p => p.ConversationId == source.ConversationId && p.UserId == userId, cancellationToken);
            if (!wasParticipant)
                return (replyValid, null, StatusCode(StatusCodes.Status403Forbidden,
                    new { message = "Можна пересилати лише повідомлення з бесід, де ви є учасником." }));

            forwardSenderName = source.SenderName;
        }

        return (replyValid, forwardSenderName, null);
    }

    private async Task<Dictionary<Guid, MessageReplyPreviewDto>> HydrateRepliesAsync(
        List<Message> messages, CancellationToken cancellationToken)
    {
        var replyIds = messages
            .Where(m => m.ReplyToMessageId.HasValue)
            .Select(m => m.ReplyToMessageId!.Value)
            .Distinct()
            .ToList();

        if (replyIds.Count == 0)
            return new Dictionary<Guid, MessageReplyPreviewDto>();

        var sources = await _db.Messages
            .Where(m => replyIds.Contains(m.Id) && !m.IsDeleted)
            .AsNoTracking()
            .ToListAsync(cancellationToken);

        return sources.ToDictionary(m => m.Id, m => new MessageReplyPreviewDto(
            m.Id,
            m.SenderId,
            m.SenderName,
            m.Type.ToString(),
            m.Text,
            m.MediaFileName));
    }

    private async Task<Dictionary<string, TrackDetails>> HydrateTracksAsync(
        List<Message> messages, Guid userId, CancellationToken cancellationToken)
    {
        var trackIds = messages
            .Where(m => m.SharedTrackId.HasValue)
            .Select(m => m.SharedTrackId!.Value.ToString())
            .Distinct()
            .ToList();

        if (trackIds.Count == 0)
            return new Dictionary<string, TrackDetails>();

        var request = new TrackInfoRequest { CurrentUserId = userId.ToString() };
        request.TrackIds.AddRange(trackIds);
        var grpcResponse = await _trackInfoClient.GetTracksInfoAsync(request, cancellationToken: cancellationToken);
        return grpcResponse.Tracks.ToDictionary(t => t.TrackId, t => t);
    }

    private async Task<ConversationDto> ToDtoAsync(Conversation c, CancellationToken cancellationToken)
    {
        MessageReplyPreviewDto? pinnedMessage = null;
        if (c.PinnedMessageId.HasValue)
        {
            var pinned = await _db.Messages
                .AsNoTracking()
                .FirstOrDefaultAsync(m => m.Id == c.PinnedMessageId.Value && !m.IsDeleted, cancellationToken);
            if (pinned != null)
                pinnedMessage = new MessageReplyPreviewDto(
                    pinned.Id, pinned.SenderId, pinned.SenderName, pinned.Type.ToString(), pinned.Text, pinned.MediaFileName);
        }

        return new ConversationDto(
            c.Id,
            c.IsGroup,
            c.Title,
            c.Participants.Select(p => new ParticipantDto(p.UserId, p.UserName, p.Role.ToString())).ToList(),
            c.CreatedAt,
            c.PinnedMessageId,
            pinnedMessage,
            c.AvatarUrl,
            c.Status.ToString(),
            c.RequestedByUserId);
    }

    private static MessageDto ToDto(Message m, Dictionary<string, TrackDetails> trackDict, Dictionary<Guid, MessageReplyPreviewDto> replyDict)
    {
        MessageReplyPreviewDto? replyTo = null;
        if (m.ReplyToMessageId.HasValue)
            replyDict.TryGetValue(m.ReplyToMessageId.Value, out replyTo);

        SharedTrackDto? trackDto = null;
        if (m.SharedTrackId.HasValue && trackDict.TryGetValue(m.SharedTrackId.Value.ToString(), out var t))
        {
            trackDto = new SharedTrackDto(
                m.SharedTrackId.Value,
                t.Title,
                t.ArtistName,
                string.IsNullOrEmpty(t.CoverImageUrl) ? null : t.CoverImageUrl,
                t.AudioUrl,
                t.DurationSeconds);
        }

        return new MessageDto(
            m.Id,
            m.ConversationId,
            m.SenderId,
            m.SenderName,
            m.Type.ToString(),
            m.Text,
            trackDto,
            m.CreatedAt,
            m.IsEdited,
            m.EditedAt,
            m.MediaUrl,
            m.MediaFileName,
            m.MediaFileSizeBytes,
            replyTo,
            m.ForwardedFromSenderName);
    }
}
