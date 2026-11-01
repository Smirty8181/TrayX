using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text;

namespace TrayX.Contracts;

public sealed class IpcClient : IAsyncDisposable
{
    private readonly string _pipeName;
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly SemaphoreSlim _connectLock = new(1, 1);
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource<IpcEnvelope>> _pending = new();

    private NamedPipeClientStream? _pipe;
    private StreamReader? _reader;
    private StreamWriter? _writer;
    private CancellationTokenSource? _lifetime;
    private Task? _readLoop;
    private int _connected;
    private int _generation;

    public IpcClient(string? pipeName = null)
    {
        _pipeName = pipeName ?? TrayXIpc.GetPipeName();
    }

    public event EventHandler? Disconnected;

    public event EventHandler<IpcEnvelope>? EventReceived;

    public bool IsConnected => Volatile.Read(ref _connected) == 1;

    public async Task ConnectAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        if (IsConnected)
        {
            return;
        }

        await _connectLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (IsConnected)
            {
                return;
            }

            await CloseTransportAsync().ConfigureAwait(false);

            var pipe = new NamedPipeClientStream(
                ".",
                _pipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous,
                System.Security.Principal.TokenImpersonationLevel.Impersonation);

            using var timeoutSource = new CancellationTokenSource(timeout);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken,
                timeoutSource.Token);

            try
            {
                await pipe.ConnectAsync(linked.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                await pipe.DisposeAsync().ConfigureAwait(false);
                throw new TimeoutException("Core не отвечает.");
            }

            var generation = Interlocked.Increment(ref _generation);
            _pipe = pipe;
            _reader = new StreamReader(pipe, new UTF8Encoding(false));
            _writer = new StreamWriter(pipe, new UTF8Encoding(false)) { AutoFlush = true };
            _lifetime = new CancellationTokenSource();
            Volatile.Write(ref _connected, 1);
            _readLoop = Task.Run(() => ReadLoopAsync(generation, _lifetime.Token), CancellationToken.None);
        }
        finally
        {
            _connectLock.Release();
        }
    }

    public async Task<T?> SendAsync<T>(
        string command,
        object? payload = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        var response = await SendEnvelopeAsync(command, payload, timeout, cancellationToken)
            .ConfigureAwait(false);
        return IpcJson.FromElement<T>(response.Payload);
    }

    public async Task SendAsync(
        string command,
        object? payload = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        await SendEnvelopeAsync(command, payload, timeout, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IpcEnvelope> SendEnvelopeAsync(
        string command,
        object? payload = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        if (!IsConnected)
        {
            throw new IpcException("Core недоступен.");
        }

        var requestId = Guid.NewGuid();
        var completion = new TaskCompletionSource<IpcEnvelope>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[requestId] = completion;

        try
        {
            await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var writer = _writer;
                if (writer is null)
                {
                    throw new IpcException("Соединение с Core закрыто.");
                }

                var line = IpcJson.Serialize(IpcEnvelope.Request(requestId, command, payload));
                await writer.WriteLineAsync(line.AsMemory(), cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _writeLock.Release();
            }

            var response = await completion.Task
                .WaitAsync(timeout ?? TimeSpan.FromSeconds(15), cancellationToken)
                .ConfigureAwait(false);

            if (response.Success == false)
            {
                throw new IpcException(response.Error ?? "Core вернул ошибку.");
            }

            return response;
        }
        catch (IOException exception)
        {
            await HandleDisconnectedAsync(Volatile.Read(ref _generation)).ConfigureAwait(false);
            throw new IpcException("Соединение с Core прервано.", exception);
        }
        finally
        {
            _pending.TryRemove(requestId, out _);
        }
    }

    private async Task ReadLoopAsync(int generation, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var reader = _reader;
                if (reader is null)
                {
                    break;
                }

                var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (line is null)
                {
                    break;
                }

                if (line.Length == 0)
                {
                    continue;
                }

                IpcEnvelope? envelope;
                try
                {
                    envelope = IpcJson.Deserialize(line);
                }
                catch (System.Text.Json.JsonException)
                {
                    continue;
                }

                if (envelope is null)
                {
                    continue;
                }

                if (envelope.Type == IpcMessageTypes.Response &&
                    envelope.Id is { } id &&
                    _pending.TryGetValue(id, out var completion))
                {
                    completion.TrySetResult(envelope);
                    continue;
                }

                if (envelope.Type == IpcMessageTypes.Event)
                {
                    RaiseEventReceived(envelope);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (IOException)
        {
        }
        finally
        {
            await HandleDisconnectedAsync(generation).ConfigureAwait(false);
        }
    }

    private void RaiseEventReceived(IpcEnvelope envelope)
    {
        try
        {
            EventReceived?.Invoke(this, envelope);
        }
        catch (Exception)
        {
            // Подписчик UI не должен ронять транспортный цикл.
        }
    }

    private async Task HandleDisconnectedAsync(int generation)
    {
        if (generation != Volatile.Read(ref _generation))
        {
            return;
        }

        if (Interlocked.Exchange(ref _connected, 0) == 0)
        {
            return;
        }

        foreach (var pair in _pending)
        {
            pair.Value.TrySetException(new IpcException("Соединение с Core потеряно."));
        }

        await CloseTransportAsync().ConfigureAwait(false);

        try
        {
            Disconnected?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception)
        {
        }
    }

    private async Task CloseTransportAsync()
    {
        var lifetime = Interlocked.Exchange(ref _lifetime, null);
        if (lifetime is not null)
        {
            await lifetime.CancelAsync().ConfigureAwait(false);
        }

        _writer = null;
        _reader = null;

        var pipe = Interlocked.Exchange(ref _pipe, null);
        if (pipe is not null)
        {
            try
            {
                await pipe.DisposeAsync().ConfigureAwait(false);
            }
            catch (IOException)
            {
            }
        }

        lifetime?.Dispose();
    }

    public async ValueTask DisposeAsync()
    {
        Interlocked.Increment(ref _generation);
        Volatile.Write(ref _connected, 0);
        await CloseTransportAsync().ConfigureAwait(false);

        if (_readLoop is not null)
        {
            try
            {
                await _readLoop.ConfigureAwait(false);
            }
            catch (Exception)
            {
            }
        }

        _writeLock.Dispose();
        _connectLock.Dispose();
    }
}
