namespace Groovra.ChatService.Microservice.Data;

public enum MessageType
{
    Text = 0,
    TrackShare = 1,
    Image = 2,
    Voice = 3,
    File = 4
}

public class Message
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ConversationId { get; set; }
    public Conversation Conversation { get; set; } = null!;

    public Guid SenderId { get; set; }
    public string SenderName { get; set; } = string.Empty;

    public MessageType Type { get; set; } = MessageType.Text;
    public string? Text { get; set; }

    // Заповнюється лише для Type == TrackShare. Сам трек Chat не зберігає —
    // тільки Guid, повні дані гідруються з Music по gRPC при читанні (як робить History).
    public Guid? SharedTrackId { get; set; }

    // Заповнюється лише для Type == Image/Voice/File. MediaUrl — публічне посилання
    // на Cloudflare R2 (FileStorageService.UploadFileAsync), файл заздалегідь
    // завантажується окремим POST /api/media/upload.
    public string? MediaUrl { get; set; }
    public string? MediaFileName { get; set; }
    public long? MediaFileSizeBytes { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public bool IsDeleted { get; set; } = false;

    public bool IsEdited { get; set; } = false;
    public DateTime? EditedAt { get; set; }

    // Відповідь на інше повідомлення в цій самій бесіді. Без FK (як SharedTrackId) —
    // якщо цитоване повідомлення видалено/недоступне, читання просто не покаже прев'ю.
    public Guid? ReplyToMessageId { get; set; }

    // Пересилання: посилання на оригінальне повідомлення (може бути з іншої бесіди) +
    // денормалізоване ім'я його автора на момент пересилання, щоб не гідрувати зайвий раз.
    public Guid? ForwardedFromMessageId { get; set; }
    public string? ForwardedFromSenderName { get; set; }
}
