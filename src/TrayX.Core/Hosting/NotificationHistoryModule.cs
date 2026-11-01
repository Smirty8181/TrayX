using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using TrayX.Application.Hosting;
using TrayX.Application.Modules;
using TrayX.Application.Notifications;
using TrayX.Contracts;
using TrayX.Core.Ipc;
using TrayX.Domain.Entities;
using TrayX.Windows;

namespace TrayX.Core.Hosting;

public sealed class NotificationHistoryModule : ITrayXModule, IClientEvents
{
    private const int RecentLimit = 200;
    private readonly INotificationStore _store;
    private readonly IpcServer _server;
    private readonly ILogger<NotificationHistoryModule> _logger;
    private Channel<NotificationWorkItem> _pending = CreatePendingQueue();

    private Task? _worker;
    private long _droppedNotifications;

    public NotificationHistoryModule(
        INotificationStore store,
        IpcServer server,
        ILogger<NotificationHistoryModule> logger)
    {
        _store = store;
        _server = server;
        _logger = logger;
    }

    public string Id => "notifications";

    public string DisplayName => "История уведомлений";

    public bool IsAvailable => WindowsSupport.IsSupported;

    public int ClientCount => _server.ClientCount;

    public long DroppedNotificationCount => Interlocked.Read(ref _droppedNotifications);

    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (_worker is null)
        {
            _pending = CreatePendingQueue();
            _worker = ProcessPendingAsync();
        }

        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_worker is null)
        {
            return;
        }

        _pending.Writer.TryComplete();
        await _worker.WaitAsync(cancellationToken).ConfigureAwait(false);
        _worker = null;
    }

    public void Publish(string name, object? payload = null)
    {
        if (string.Equals(name, IpcEvents.Toast, StringComparison.Ordinal) &&
            payload is ToastEvent toast)
        {
            var notification = new NotificationRecord(
                Guid.NewGuid(),
                toast.Title,
                toast.Message,
                DateTimeOffset.UtcNow);
            if (!_pending.Writer.TryWrite(new PersistNotification(notification)))
            {
                var droppedCount = Interlocked.Increment(ref _droppedNotifications);
                _logger.LogError(
                    "Notification history queue is closed or full; notification was not stored ({DroppedCount} total dropped)",
                    droppedCount);
            }
        }

        _server.Publish(name, payload);
    }

    public async Task<IReadOnlyList<NotificationDto>> GetRecentAsync(
        CancellationToken cancellationToken = default)
    {
        var completion = new TaskCompletionSource<IReadOnlyList<NotificationDto>>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        await WriteWorkItemAsync(new ReadNotifications(completion), cancellationToken)
            .ConfigureAwait(false);
        return await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await WriteWorkItemAsync(new ClearNotifications(completion), cancellationToken)
            .ConfigureAwait(false);
        await completion.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task ProcessPendingAsync()
    {
        Exception? failure = null;
        try
        {
            await foreach (var workItem in _pending.Reader.ReadAllAsync().ConfigureAwait(false))
            {
                switch (workItem)
                {
                    case PersistNotification persist:
                        await PersistAsync(persist.Notification).ConfigureAwait(false);
                        break;
                    case ReadNotifications read:
                        await ReadRecentAsync(read.Completion).ConfigureAwait(false);
                        break;
                    case ClearNotifications clear:
                        await ClearAsync(clear.Completion).ConfigureAwait(false);
                        break;
                }
            }
        }
        catch (Exception exception)
        {
            failure = exception;
            _logger.LogCritical(exception, "Notification history worker stopped unexpectedly");
            throw;
        }
        finally
        {
            _pending.Writer.TryComplete(failure);
            var terminalFailure = failure ??
                new InvalidOperationException("Notification history worker has stopped.");
            while (_pending.Reader.TryRead(out var workItem))
            {
                FailPending(workItem, terminalFailure);
            }
        }
    }

    private async Task WriteWorkItemAsync(NotificationWorkItem workItem, CancellationToken cancellationToken)
    {
        var worker = _worker;
        if (worker is null)
        {
            throw new InvalidOperationException("Notification history module is not running.");
        }

        if (worker.IsFaulted)
        {
            throw new InvalidOperationException("Notification history worker failed.", worker.Exception);
        }

        await _pending.Writer.WriteAsync(workItem, cancellationToken).ConfigureAwait(false);
    }

    private static void FailPending(NotificationWorkItem workItem, Exception exception)
    {
        switch (workItem)
        {
            case ReadNotifications read:
                read.Completion.TrySetException(exception);
                break;
            case ClearNotifications clear:
                clear.Completion.TrySetException(exception);
                break;
        }
    }

    private static Channel<NotificationWorkItem> CreatePendingQueue() =>
        Channel.CreateBounded<NotificationWorkItem>(
            new BoundedChannelOptions(512)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = false
            });

    private async Task PersistAsync(NotificationRecord notification)
    {
        try
        {
            await _store.AddAsync(notification).ConfigureAwait(false);
            _server.Publish(IpcEvents.NotificationHistoryChanged);
        }
        catch (Exception exception)
        {
            _logger.LogError(
                exception,
                "Could not persist notification {NotificationId}",
                notification.Id);
        }
    }

    private async Task ClearAsync(TaskCompletionSource completion)
    {
        try
        {
            await _store.ClearAsync().ConfigureAwait(false);
            _server.Publish(IpcEvents.NotificationHistoryChanged);
            completion.SetResult();
        }
        catch (Exception exception)
        {
            completion.SetException(exception);
        }
    }

    private async Task ReadRecentAsync(
        TaskCompletionSource<IReadOnlyList<NotificationDto>> completion)
    {
        try
        {
            var records = await _store.GetRecentAsync(RecentLimit).ConfigureAwait(false);
            completion.SetResult(records.Select(ToDto).ToArray());
        }
        catch (Exception exception)
        {
            completion.SetException(exception);
        }
    }

    private static NotificationDto ToDto(NotificationRecord notification) => new(
        notification.Id,
        notification.Title,
        notification.Message,
        notification.CreatedAt);

    private abstract record NotificationWorkItem;

    private sealed record PersistNotification(NotificationRecord Notification) : NotificationWorkItem;

    private sealed record ReadNotifications(
        TaskCompletionSource<IReadOnlyList<NotificationDto>> Completion) : NotificationWorkItem;

    private sealed record ClearNotifications(TaskCompletionSource Completion) : NotificationWorkItem;
}
