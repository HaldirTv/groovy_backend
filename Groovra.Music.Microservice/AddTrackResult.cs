namespace Groovra.Music.Microservice.Result;

public enum AddTrackResult
{
    Added,
    AlreadyExists,
    PlaylistNotFound,
    TrackNotFound,
    AccessDenied
}