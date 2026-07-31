namespace Groovra.Music.Microservice.Result;

public class BulkTrackOperationResult
{
    public bool IsAlbumNotFound { get; set; }

    public List<Guid> AddedIds { get; set; } = new();

    public List<Guid> AlreadyInAlbumIds { get; set; } = new();

    public List<Guid> BelongsToAnotherAlbumIds { get; set; } = new();

    /// <summary>
    /// Треки, які належать альбому, що зараз лежить у кошику. Виділені окремо від
    /// BelongsToAnotherAlbumIds, бо для юзера це глухий кут: альбом-блокувальник не видно
    /// в жодному списку, тож повідомлення "трек уже в іншому альбомі" виглядало б брехнею.
    /// Щоб звільнити такий трек, альбом треба або відновити й прибрати трек, або видалити
    /// остаточно.
    /// </summary>
    public List<Guid> BelongsToTrashedAlbumIds { get; set; } = new();

    public List<Guid> NotFoundIds { get; set; } = new();

    public bool HasChanges => AddedIds.Any();
}