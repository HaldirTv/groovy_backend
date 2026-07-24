namespace Groovra.ChatService.Microservice.DTOS;

// Title/AvatarUrl null означает "не менять это поле" — розрізняємо null (не чіпати)
// і порожній рядок (свідомо очистити), тому пропускаємо валідацію на порожній Title лише
// коли він явно переданий.
public record UpdateGroupInfoRequest(string? Title, string? AvatarUrl);
