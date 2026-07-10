namespace Groovra.Music.Microservice.Result;

/// <summary>
/// Результат масової операції додавання треків до альбому.
/// </summary>
public class BulkTrackOperationResult
{
    /// <summary>Флаг, чи знайшли ми сам альбом.</summary>
    public bool IsAlbumNotFound { get; set; }

    /// <summary>Список ID треків, які успішно прив'язані до альбому.</summary>
    public List<Guid> AddedIds { get; set; } = new();

    /// <summary>Список ID треків, які вже були в цьому альбомі (пропущені).</summary>
    public List<Guid> AlreadyInAlbumIds { get; set; } = new();

    /// <summary>Список ID треків, які вже належать ІНШОМУ альбому (не можна перезаписувати).</summary>
    public List<Guid> BelongsToAnotherAlbumIds { get; set; } = new();

    /// <summary>Список ID треків, яких взагалі немає в базі даних.</summary>
    public List<Guid> NotFoundIds { get; set; } = new();

    /// <summary>Узагальнений показник: чи змінилося щось у базі.</summary>
    public bool HasChanges => AddedIds.Any();
}