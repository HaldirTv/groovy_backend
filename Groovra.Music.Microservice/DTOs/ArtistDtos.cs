namespace Groovra.Music.Microservice.DTOs;

public record ArtistListItemDto(string Name, int TrackCount, int AlbumCount, string? AvatarUrl, long TotalPlayCount);

public record PagedArtistResultDto(List<ArtistListItemDto> Items, int TotalCount, int PageNumber, int PageSize);
