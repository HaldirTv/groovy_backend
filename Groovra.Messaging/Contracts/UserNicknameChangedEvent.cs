namespace Groovra.Messaging.Contracts;

public record UserNicknameChangedEvent(
    Guid UserId,
    string NewNickname,
    DateTime UpdatedAt
);
