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

    // GET /chat/conversations — список бесід поточного юзера, найновіші зверху.
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

        // "Видалено для мене" повідомлення теж не повинні потрапляти в прев'ю останнього
        // повідомлення — рахуємо їх разом з ClearedAt нижче, в пам'яті, бо фільтр залежить
        // від конкретного юзера й різний для кожної бесіди.
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

    // GET /chat/conversations/{id} — картка бесіди (учасники, тип), тільки для учасника.
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

    // POST /chat/conversations — створити/прикріпити бесіду. Для 1:1 повертає вже
    // існуючу бесіду між цими двома юзерами замість дубля.
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

            // Це справді перша бесіда між цією парою (дедуп вище нічого не знайшов) —
            // якщо хтось із них когось заблокував, нову Pending-бесіду створювати не можна,
            // інакше заблокований/відхилений користувач міг би спамити новими запитами.
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
            // Групи завжди Active. Щойно створена 1:1-бесіда стартує як запит (Pending) —
            // отримувач побачить кнопки Прийняти/Відхилити замість поля вводу, поки не
            // погодиться (ConversationsController.SendMessage тощо блокують надсилання
            // повідомлень від будь-кого, крім ініціатора, доки статус Pending).
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

    // DELETE /chat/conversations/{id} — вихід поточного юзера з бесіди; коли виходить
    // останній учасник, бесіда і всі повідомлення видаляються каскадом, а їхні медіафайли
    // додатково прибираються з Cloudflare R2 (щоб не лишався сміттєвий об'єкт у бакеті).
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

        // Якщо це був останній учасник — Conversation (і всі Messages каскадом) зникає
        // назавжди, тож MediaUrl треба забрати ДО видалення, інакше R2-файли лишаться
        // сиротами: посилатись на них уже не буде звідки.
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

        // Якщо виходить Admin групи, а інші учасники лишаються — права переходять до
        // того, хто в групі найдовше (найраніший JoinedAt), інакше група лишилась би
        // без адміна назавжди. Для 1:1 (IsGroup=false) ця гілка ніколи не спрацьовує.
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
            // Для групи шлемо окрему явну подію GroupDeleted (щоб фронтенд міг
            // однозначно відрізнити "група зникла повністю" від 1:1 ConversationDeleted).
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
        // 1:1, не останній учасник (інша сторона лишилась) — подія й раніше не шлеться,
        // поведінка навмисно не змінюється.

        if (isLastParticipant)
            await DeleteMediaForMessagesAsync(mediaUrls, cancellationToken);

        return NoContent();
    }

    // POST /chat/conversations/{id}/clear — очистити історію бесіди. За замовчуванням
    // (ForBoth=false) тільки для себе — решта учасників і надалі бачать усе без змін.
    // ForBoth=true фізично ховає (IsDeleted=true) усі повідомлення для ВСІХ учасників
    // одразу і, на відміну від DELETE .../all, не чіпає саму Conversation/Participants —
    // чат лишається відкритим, просто порожнім, і подальші повідомлення йдуть у нього ж.
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
            // У групі стерти історію для ВСІХ учасників може лише Admin — той самий рівень
            // прав, що й видалення групи/учасників/редагування назви-аватарки. Без цієї
            // перевірки будь-який рядовий учасник міг стерти всю переписку іншим.
            // У 1:1-бесіді обмеження немає (там сторони рівноправні, як і в DeleteConversationForBoth).
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

            // Медіа видаляємо з R2 вже ПІСЛЯ успішного SaveChangesAsync — щоб не лишити
            // повідомлення з MediaUrl, що вказує на вже стертий з бакета файл, якщо БД-запит
            // раптом впаде.
            await DeleteMediaForMessagesAsync(mediaUrls, cancellationToken);
        }
        else
        {
            membership.ClearedAt = DateTime.UtcNow;
            await _db.SaveChangesAsync(cancellationToken);
        }

        return NoContent();
    }

    // GET /chat/conversations/{id}/messages/search?query=... — пошук за текстом у межах
    // бесіди, з тими самими фільтрами видимості (ClearedAt, MessageDeletions, IsDeleted),
    // що й звичайна історія. TrackShare-повідомлення (Text=null) у результати не потрапляють.
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

    // DELETE /chat/conversations/{id}/all — повне видалення бесіди для всіх учасників одразу
    // (на відміну від DELETE /chat/conversations/{id}, яке лише виводить викликача з бесіди).
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

        // У групі повністю видалити чат для всіх може лише Admin (творець групи) —
        // у 1:1 такого обмеження немає, там кожна сторона рівноправна.
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

    // Спільна логіка для DELETE .../all і POST .../decline (Відхилити запит на переписку) —
    // повне видалення бесіди для всіх учасників разом з чисткою R2-медіа. eventName дає
    // виклику визначити назву SignalR-події (GroupDeleted для групи, інакше ConversationDeletedForBoth).
    private async Task DeleteConversationForBothInternalAsync(Conversation conversation, Guid userId, string eventName, CancellationToken cancellationToken)
    {
        var id = conversation.Id;

        // Треба забрати MediaUrl ДО видалення — каскад (ChatDbContext: Conversation.Messages,
        // OnDelete Cascade) прибере рядки Messages разом із Conversation.
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

    // POST /chat/conversations/{id}/accept — отримувач запиту на переписку погоджується;
    // Pending -> Active, звичайне поле вводу з'являється для обох.
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

    // POST /chat/conversations/{id}/decline — отримувач відхиляє запит на переписку:
    // блокує ініціатора (щоб той не міг одразу створити новий Pending-запит, див.
    // CreateConversation) і повністю видаляє бесіду для обох, як DELETE .../all.
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

    // PATCH /chat/conversations/{id}/group — змінити назву і/або аватарку групи.
    // Лише Admin (той самий рівень прав, що й видалення групи/учасників). Title/AvatarUrl
    // null у запиті = поле не чіпати; порожній рядок для Title відхиляється валідацією.
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

        // Стару аватарку прибираємо з R2 вже ПІСЛЯ успішного SaveChangesAsync — щоб не
        // лишити конверсацію з посиланням на вже стертий файл, якщо БД-запит впаде.
        if (avatarChanged && !string.IsNullOrEmpty(oldAvatarUrl))
            await _fileStorage.DeleteFileAsync(oldAvatarUrl, cancellationToken);

        return Ok(await ToDtoAsync(conversation, cancellationToken));
    }

    // POST /chat/conversations/{id}/participants — додати учасників у групову бесіду.
    // Будь-який поточний учасник групи може додавати нових (кік, на відміну від цього,
    // обмежений роллю Admin — див. RemoveParticipant).
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

    // DELETE /chat/conversations/{id}/participants/{userId} — виключити учасника з групи.
    // Лише Admin (творець групи), кожен інший отримує 403. Самого себе кікнути не можна —
    // для цього є DELETE /chat/conversations/{id} (вихід із бесіди).
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

    // GET /chat/conversations/{id}/messages — історія повідомлень (пагінація, найновіші спочатку на вході,
    // повертаються вже у хронологічному порядку). Не показує повідомлення, надіслані до ClearedAt
    // поточного учасника, і повідомлення, видалені ним "тільки для себе".
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

    // POST /chat/conversations/{id}/messages — надіслати звичайне текстове повідомлення.
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

    // POST /chat/conversations/{id}/messages/track — поділитися треком у чаті.
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

    // POST /chat/conversations/{id}/messages/media — надіслати повідомлення з медіа
    // (картинка/голосове/файл), уже завантаженим через POST /api/media/upload.
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

    // PUT /chat/conversations/{id}/messages/{messageId} — редагування власного текстового
    // повідомлення. Трек-шеринг не редагується.
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

    // POST /chat/conversations/{id}/messages/{messageId}/delete — видалення повідомлення:
    // ForEveryone=true (лише автор) прибирає його для всіх, ForEveryone=false (будь-який
    // учасник) ховає його тільки для викликача.
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

            // Якщо видалене повідомлення було закріплене — знімаємо закріплення, інакше
            // банер "закріплене повідомлення" лишиться посилатись на приховане повідомлення.
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

            // "Для всіх" — файл більше нікому не потрібен, чистимо R2. "Для мене" (гілка
            // else нижче) файл НЕ чіпаємо — він і надалі має бути доступний співрозмовнику.
            // Пересилання (ForwardedFromMessageId) дозволяє кільком Message ділити один
            // MediaUrl, тож фізично видаляємо з R2 лише коли жодне інше живе повідомлення
            // вже не посилається на той самий файл.
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

    // POST /chat/conversations/{id}/messages/{messageId}/pin — закріпити повідомлення.
    // Одне закріплене повідомлення на бесіду: закріплення нового просто замінює попереднє.
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

    // DELETE /chat/conversations/{id}/pin — зняти закріплення поточного повідомлення.
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

    // Блокування діє лише в 1:1-бесідах (як у Telegram) — у групах ігнорується, бо не
    // зрозуміло, кого саме з групи блокувати. Повертає true, якщо надсилання має бути
    // заборонене (будь-який напрямок блокування між викликачем і іншим учасником).
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

    // Поки бесіда Pending, надсилати повідомлення може лише той, хто її ініціював —
    // отримувач спочатку має натиснути "Прийняти" (POST .../accept).
    private async Task<bool> IsPendingAndNotRequesterAsync(Guid conversationId, Guid userId, CancellationToken cancellationToken)
    {
        var conversation = await _db.Conversations
            .AsNoTracking()
            .FirstOrDefaultAsync(c => c.Id == conversationId, cancellationToken);

        return conversation != null
            && conversation.Status == ConversationStatus.Pending
            && conversation.RequestedByUserId != userId;
    }

    // Спільний хелпер для "видалити для всіх"/"очистити для всіх"/"видалити бесіду для
    // всіх" — послідовно чистить R2 для кожного непорожнього MediaUrl. FileStorageService
    // сама ковтає помилки окремого файлу (best effort), тож один зіпсований URL не спинить
    // видалення решти. Викликається ПІСЛЯ SaveChangesAsync, тож перевірка "чи лишилось
    // живе повідомлення з цим MediaUrl" (важливо через пересилання — кілька Message можуть
    // ділити один файл) вже бачить актуальний стан БД.
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
            // Auth тимчасово недоступний або юзера не знайдено — не валимо створення
            // бесіди через це, ім'я просто підтягнеться пізніше при оновленні профілю.
            return string.Empty;
        }
    }

    // Перевіряє ReplyToMessageId (має бути в тій самій бесіді, не видалене) і
    // ForwardedFromMessageId (викликач мав бути учасником бесіди-джерела — інакше 403,
    // щоб не можна було переслати контент, до якого ніколи не мав доступу). Повертає
    // (чи валідний ReplyTo, ім'я автора для пересилання, або помилку для return).
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

    // Батчева підгрузка прев'ю для Message.ReplyToMessageId — свідомо БЕЗ фільтрів
    // видимості (ClearedAt/MessageDeletions), щоб цитоване повідомлення й далі показувало
    // прев'ю навіть якщо очищене саме для цього юзера. Видалені (IsDeleted) пропускаються.
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
