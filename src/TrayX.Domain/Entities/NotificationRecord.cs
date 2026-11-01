namespace TrayX.Domain.Entities;

public sealed record NotificationRecord(
    Guid Id,
    string Title,
    string Message,
    DateTimeOffset CreatedAt);
