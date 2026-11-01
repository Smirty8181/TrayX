using TrayX.Domain.Entities;

namespace TrayX.Application.Notifications;

public interface INotificationStore
{
    Task<IReadOnlyList<NotificationRecord>> GetRecentAsync(
        int limit,
        CancellationToken cancellationToken = default);

    Task AddAsync(NotificationRecord notification, CancellationToken cancellationToken = default);

    Task ClearAsync(CancellationToken cancellationToken = default);
}
