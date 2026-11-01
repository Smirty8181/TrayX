using Microsoft.Extensions.Logging.Abstractions;
using TrayX.Application.Notifications;
using TrayX.Contracts;
using TrayX.Core.Hosting;
using TrayX.Core.Ipc;
using TrayX.Domain.Entities;

namespace TrayX.IntegrationTests;

public sealed class NotificationHistoryModuleIntegrationTests
{
    [Fact]
    public async Task ClearAsync_WaitsForEarlierNotificationsBeforeClearingHistory()
    {
        var store = new BlockingNotificationStore();
        var server = new IpcServer($"TrayX-test-{Guid.NewGuid():N}", NullLogger<IpcServer>.Instance);
        var module = new NotificationHistoryModule(
            store,
            server,
            NullLogger<NotificationHistoryModule>.Instance);
        await module.StartAsync(CancellationToken.None);

        try
        {
            module.Publish(IpcEvents.Toast, new ToastEvent("Title", "Message"));
            await store.AddStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

            var readTask = module.GetRecentAsync();
            var clearTask = module.ClearAsync();
            Assert.False(readTask.IsCompleted);
            store.AllowAddToFinish.TrySetResult();

            var notifications = await readTask.WaitAsync(TimeSpan.FromSeconds(5));
            await clearTask.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Single(notifications);
            Assert.Empty(await module.GetRecentAsync());
        }
        finally
        {
            store.AllowAddToFinish.TrySetResult();
            await module.StopAsync(CancellationToken.None);
            await server.DisposeAsync();
        }
    }

    [Fact]
    public async Task Publish_CountsNotificationsDroppedWhenTheBoundedQueueIsFull()
    {
        var store = new BlockingNotificationStore();
        var server = new IpcServer($"TrayX-test-{Guid.NewGuid():N}", NullLogger<IpcServer>.Instance);
        var module = new NotificationHistoryModule(
            store,
            server,
            NullLogger<NotificationHistoryModule>.Instance);
        await module.StartAsync(CancellationToken.None);

        try
        {
            module.Publish(IpcEvents.Toast, new ToastEvent("Title", "In flight"));
            await store.AddStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

            for (var index = 0; index < 514; index++)
            {
                module.Publish(IpcEvents.Toast, new ToastEvent("Title", $"Queued {index}"));
            }

            Assert.Equal(2, module.DroppedNotificationCount);
        }
        finally
        {
            store.AllowAddToFinish.TrySetResult();
            await module.StopAsync(CancellationToken.None);
            await server.DisposeAsync();
        }
    }

    [Fact]
    public async Task ReadAfterStop_FailsPromptlyInsteadOfWaitingForAnUnservicedRequest()
    {
        var store = new BlockingNotificationStore();
        var server = new IpcServer($"TrayX-test-{Guid.NewGuid():N}", NullLogger<IpcServer>.Instance);
        var module = new NotificationHistoryModule(
            store,
            server,
            NullLogger<NotificationHistoryModule>.Instance);
        await module.StartAsync(CancellationToken.None);
        await module.StopAsync(CancellationToken.None);

        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => module.GetRecentAsync().WaitAsync(TimeSpan.FromSeconds(1)));
        }
        finally
        {
            await server.DisposeAsync();
        }
    }

    [Fact]
    public async Task Restart_CreatesAWorkingNotificationQueue()
    {
        var store = new BlockingNotificationStore();
        store.AllowAddToFinish.TrySetResult();
        var server = new IpcServer($"TrayX-test-{Guid.NewGuid():N}", NullLogger<IpcServer>.Instance);
        var module = new NotificationHistoryModule(
            store,
            server,
            NullLogger<NotificationHistoryModule>.Instance);

        await module.StartAsync(CancellationToken.None);
        await module.StopAsync(CancellationToken.None);
        await module.StartAsync(CancellationToken.None);

        try
        {
            module.Publish(IpcEvents.Toast, new ToastEvent("Title", "After restart"));
            await store.AddStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));

            var notifications = await module.GetRecentAsync().WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Single(notifications);
            Assert.Equal("After restart", notifications[0].Message);
        }
        finally
        {
            await module.StopAsync(CancellationToken.None);
            await server.DisposeAsync();
        }
    }

    private sealed class BlockingNotificationStore : INotificationStore
    {
        private readonly List<NotificationRecord> _notifications = [];

        public TaskCompletionSource AddStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource AllowAddToFinish { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<IReadOnlyList<NotificationRecord>> GetRecentAsync(
            int limit,
            CancellationToken cancellationToken = default)
        {
            IReadOnlyList<NotificationRecord> result = _notifications
                .OrderByDescending(notification => notification.CreatedAt)
                .Take(limit)
                .ToArray();
            return Task.FromResult(result);
        }

        public async Task AddAsync(
            NotificationRecord notification,
            CancellationToken cancellationToken = default)
        {
            AddStarted.TrySetResult();
            await AllowAddToFinish.Task.WaitAsync(cancellationToken);
            _notifications.Add(notification);
        }

        public Task ClearAsync(CancellationToken cancellationToken = default)
        {
            _notifications.Clear();
            return Task.CompletedTask;
        }
    }
}
