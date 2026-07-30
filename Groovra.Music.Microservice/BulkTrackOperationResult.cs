namespace Groovra.Music.Microservice.Result;

public class BulkTrackOperationResult
{
    public bool IsAlbumNotFound { get; set; }

    public List<Guid> AddedIds { get; set; } = new();

    public List<Guid> AlreadyInAlbumIds { get; set; } = new();

    public List<Guid> BelongsToAnotherAlbumIds { get; set; } = new();

    public List<Guid> NotFoundIds { get; set; } = new();

    public bool HasChanges => AddedIds.Any();
}