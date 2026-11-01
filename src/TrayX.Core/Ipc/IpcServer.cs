using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using TrayX.Application.Hosting;
using TrayX.Contracts;

namespace TrayX.Core.Ipc;

public sealed class IpcServer : IClientEvents, IDisposable, IAsyncDisposable
{
    private const int MaxClients = 8;
    private const int MaxMessageLength = 1_048_576;
    private const int MaxQueuedMessagesPerClient = 128;
    private static readonly TimeSpan DefaultHandlerTimeout = TimeSpan.FromSeconds(14);

    private readonly string _pipeName;
    private readonly ILogger<IpcServer> _logger;
    private readonly TimeSpan _handlerTimeout;
    private readonly ConcurrentDictionary<Guid, ClientConnection> _connections = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _listenerSync = new();
    private readonly object _stopSync = new();

    private Func<IpcEnvelope, CancellationToken, Task<IpcEnvelope>>? _handler;
    private NamedPipeServerStream? _listener;
    private Task? _acceptLoop;
    private Task? _stopTask;
    private int _started;
    private int _disposed;

    public IpcServer(string pipeName, ILogger<IpcServer> logger, TimeSpan? handlerTimeout = null)
    {
        _pipeName = pipeName;
        _logger = logger;
        _handlerTimeout = handlerTimeout ?? DefaultHandlerTimeout;
        if (_handlerTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(handlerTimeout), "IPC handler timeout must be positive.");
        }
    }

    public int ClientCount => _connections.Count;

    public void ConfigureHandler(Func<IpcEnvelope, CancellationToken, Task<IpcEnvelope>> handler)
    {
        _handler = handler ?? throw new ArgumentNullException(nameof(handler));
    }

    public void Start()
    {
        if (_handler is null)
        {
            throw new InvalidOperationException("IPC handler is not configured.");
        }

        if (Interlocked.Exchange(ref _started, 1) == 1)
        {
            return;
        }

        _acceptLoop = AcceptLoopAsync(_lifetime.Token);
        _logger.LogInformation("IPC server listening on pipe {Pipe}", _pipeName);
    }

    public void Publish(string name, object? payload = null)
    {
        if (_connections.IsEmpty)
        {
            return;
        }

        var line = IpcJson.Serialize(IpcEnvelope.Event(name, payload));
        foreach (var connection in _connections.Values)
        {
            connection.Send(line);
        }
    }

    public Task PublishAsync(string name, object? payload = null)
    {
        if (_connections.IsEmpty)
        {
            return Task.CompletedTask;
        }

        var line = IpcJson.Serialize(IpcEnvelope.Event(name, payload));
        return Task.WhenAll(_connections.Values.Select(connection => connection.SendAsync(line)));
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            NamedPipeServerStream? pipe = null;
            try
            {
                pipe = new NamedPipeServerStream(
                    _pipeName,
                    PipeDirection.InOut,
                    MaxClients,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);

                lock (_listenerSync)
                {
                    _listener = pipe;
                }

                await pipe.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);

                lock (_listenerSync)
                {
                    if (ReferenceEquals(_listener, pipe))
                    {
                        _listener = null;
                    }
                }

                if (cancellationToken.IsCancellationRequested)
                {
                    await pipe.DisposeAsync().ConfigureAwait(false);
                    break;
                }

                if (_connections.Count >= MaxClients)
                {
                    _logger.LogWarning("IPC connection rejected: limit of {Limit} reached", MaxClients);
                    await pipe.DisposeAsync().ConfigureAwait(false);
                    continue;
                }

                var connection = new ClientConnection(
                    Guid.NewGuid(),
                    pipe,
                    _handler!,
                    OnConnectionClosed,
                    _logger,
                    _handlerTimeout);

                _connections[connection.Id] = connection;
                connection.Start();
                _logger.LogInformation("UI connected over IPC ({Count} clients)", _connections.Count);
                pipe = null;
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (IOException exception)
            {
                if (!cancellationToken.IsCancellationRequested)
                {
                    _logger.LogDebug(exception, "IPC accept failed");
                }

                if (pipe is not null)
                {
                    await pipe.DisposeAsync().ConfigureAwait(false);
                }

                if (!cancellationToken.IsCancellationRequested)
                {
                    await Task.Delay(250, cancellationToken).ConfigureAwait(false);
                }
            }
            finally
            {
                lock (_listenerSync)
                {
                    if (ReferenceEquals(_listener, pipe))
                    {
                        _listener = null;
                    }
                }
            }
        }
    }

    private void OnConnectionClosed(Guid id)
    {
        if (_connections.TryRemove(id, out _))
        {
            _logger.LogInformation("UI disconnected from IPC ({Count} clients)", _connections.Count);
        }
    }

    public Task StopAsync()
    {
        lock (_stopSync)
        {
            return _stopTask ??= StopCoreAsync();
        }
    }

    private async Task StopCoreAsync()
    {
        await _lifetime.CancelAsync().ConfigureAwait(false);

        lock (_listenerSync)
        {
            try
            {
                _listener?.Dispose();
            }
            catch (IOException)
            {
            }

            _listener = null;
        }

        var connections = _connections.Values.ToArray();
        foreach (var connection in connections)
        {
            connection.Close();
        }

        try
        {
            try
            {
                await Task.WhenAll(connections.Select(connection => connection.Completion))
                    .WaitAsync(TimeSpan.FromSeconds(3))
                    .ConfigureAwait(false);
            }
            catch (TimeoutException exception)
            {
                _logger.LogWarning(
                    exception,
                    "Timed out waiting for {Count} IPC client connection(s) to stop",
                    connections.Count(connection => !connection.Completion.IsCompleted));
            }
        }
        finally
        {
            try
            {
                if (_acceptLoop is not null)
                {
                    try
                    {
                        await _acceptLoop.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
                    }
                    catch (TimeoutException exception)
                    {
                        _logger.LogWarning(exception, "Timed out waiting for the IPC accept loop to stop");
                    }
                }
            }
            finally
            {
                _connections.Clear();
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        try
        {
            await StopAsync().ConfigureAwait(false);
        }
        finally
        {
            _lifetime.Dispose();
        }
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    private sealed class ClientConnection
    {
        private readonly Guid _id;
        private readonly NamedPipeServerStream _pipe;
        private readonly Func<IpcEnvelope, CancellationToken, Task<IpcEnvelope>> _handler;
        private readonly Action<Guid> _closed;
        private readonly ILogger _logger;
        private readonly TimeSpan _handlerTimeout;
        private readonly Channel<OutgoingMessage> _outgoing = Channel.CreateBounded<OutgoingMessage>(
            new BoundedChannelOptions(MaxQueuedMessagesPerClient)
            {
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = false
            });
        private readonly CancellationTokenSource _lifetime = new();
        private readonly char[] _readBuffer = new char[4096];

        private StreamReader? _reader;
        private StreamWriter? _writer;
        private Task? _readLoop;
        private Task? _sendLoop;
        private Task? _completion;
        private int _readBufferPosition;
        private int _readBufferLength;
        private int _closedFlag;
        private int _resourcesDisposed;
        private long _droppedMessages;

        public ClientConnection(
            Guid id,
            NamedPipeServerStream pipe,
            Func<IpcEnvelope, CancellationToken, Task<IpcEnvelope>> handler,
            Action<Guid> closed,
            ILogger logger,
            TimeSpan handlerTimeout)
        {
            _id = id;
            _pipe = pipe;
            _handler = handler;
            _closed = closed;
            _logger = logger;
            _handlerTimeout = handlerTimeout;
        }

        public Guid Id => _id;

        public Task Completion => _completion ?? Task.CompletedTask;

        public void Start()
        {
            var encoding = new UTF8Encoding(false);
            _reader = new StreamReader(_pipe, encoding);
            _writer = new StreamWriter(_pipe, encoding) { AutoFlush = true };
            _sendLoop = Task.Run(SendLoopAsync, CancellationToken.None);
            _readLoop = Task.Run(ReadLoopAsync, CancellationToken.None);
            _completion = Task.WhenAll(_readLoop, _sendLoop);
        }

        public void Send(string line)
        {
            if (Volatile.Read(ref _closedFlag) == 1)
            {
                return;
            }

            if (!_outgoing.Writer.TryWrite(new OutgoingMessage(line, null)))
            {
                var dropped = Interlocked.Increment(ref _droppedMessages);
                if (dropped == 1 || dropped % 100 == 0)
                {
                    _logger.LogWarning(
                        "Dropped {Count} IPC event(s) for client {ClientId}; outgoing queue limit is {Limit}",
                        dropped,
                        _id,
                        MaxQueuedMessagesPerClient);
                }
            }
        }

        public Task SendAsync(string line)
        {
            if (Volatile.Read(ref _closedFlag) == 1)
            {
                return Task.CompletedTask;
            }

            return SendLineAsync(line);
        }

        private async Task SendLineAsync(string line)
        {
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            try
            {
                await _outgoing.Writer.WriteAsync(new OutgoingMessage(line, completion), _lifetime.Token)
                    .ConfigureAwait(false);
                await completion.Task.WaitAsync(_lifetime.Token).ConfigureAwait(false);
            }
            catch (Exception exception) when (
                exception is OperationCanceledException or IOException or ObjectDisposedException or
                    ChannelClosedException)
            {
                Close();
            }
        }

        private async Task SendLoopAsync()
        {
            try
            {
                await foreach (var message in _outgoing.Reader.ReadAllAsync(_lifetime.Token).ConfigureAwait(false))
                {
                    try
                    {
                        var writer = _writer;
                        if (writer is null)
                        {
                            throw new ObjectDisposedException(nameof(StreamWriter));
                        }

                        await writer.WriteLineAsync(message.Line.AsMemory(), _lifetime.Token).ConfigureAwait(false);
                        message.Completion?.TrySetResult();
                    }
                    catch (Exception exception) when (
                        exception is OperationCanceledException or IOException or ObjectDisposedException)
                    {
                        message.Completion?.TrySetException(exception);
                        throw;
                    }
                }
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested)
            {
            }
            catch (ChannelClosedException)
            {
            }
            catch (Exception exception) when (exception is IOException or ObjectDisposedException)
            {
                _logger.LogDebug(exception, "IPC outgoing queue failed for client {ClientId}", _id);
                Close();
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Unexpected IPC sender failure for client {ClientId}", _id);
                Close();
            }
            finally
            {
                while (_outgoing.Reader.TryRead(out var pending))
                {
                    pending.Completion?.TrySetCanceled();
                }
            }
        }

        private async Task ReadLoopAsync()
        {
            try
            {
                while (!_lifetime.IsCancellationRequested)
                {
                    var reader = _reader;
                    if (reader is null)
                    {
                        break;
                    }

                    var line = await ReadBoundedLineAsync(reader, _lifetime.Token).ConfigureAwait(false);
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
                    catch (System.Text.Json.JsonException exception)
                    {
                        _logger.LogDebug(exception, "Malformed IPC message ignored");
                        continue;
                    }

                    if (envelope is null ||
                        envelope.Type != IpcMessageTypes.Request ||
                        envelope.Id is not { } requestId ||
                        string.IsNullOrWhiteSpace(envelope.Name))
                    {
                        continue;
                    }

                    IpcEnvelope response;
                    using var handlerTimeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
                    handlerTimeout.CancelAfter(_handlerTimeout);
                    Task<IpcEnvelope>? handlerTask = null;
                    try
                    {
                        handlerTask = _handler(envelope, handlerTimeout.Token);
                        response = await handlerTask.WaitAsync(handlerTimeout.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (
                        handlerTimeout.IsCancellationRequested && !_lifetime.IsCancellationRequested)
                    {
                        _logger.LogWarning(
                            "IPC command {Command} exceeded the {Timeout} timeout",
                            envelope.Name,
                            _handlerTimeout);
                        if (handlerTask is not null)
                        {
                            _ = ObserveHandlerAsync(handlerTask, envelope.Name!);
                        }

                        response = IpcEnvelope.Failure(requestId, "Время ожидания IPC-команды истекло.");
                        await SendLineAsync(IpcJson.Serialize(response)).ConfigureAwait(false);
                        break;
                    }
                    catch (Exception exception)
                    {
                        _logger.LogWarning(exception, "IPC command {Command} failed", envelope.Name);
                        response = IpcEnvelope.Failure(requestId, "The IPC command failed. See the TrayX log for details.");
                    }

                    if (response.Type != IpcMessageTypes.Response)
                    {
                        response = IpcEnvelope.Response(requestId, null);
                    }

                    await SendLineAsync(IpcJson.Serialize(response)).ConfigureAwait(false);
                }
            }
            catch (MessageTooLargeException exception)
            {
                _logger.LogWarning(exception, "IPC message exceeded the {Limit}-character limit", MaxMessageLength);
            }
            catch (OperationCanceledException)
            {
            }
            catch (IOException)
            {
            }
            finally
            {
                Close();
                await DisposeResourcesAsync().ConfigureAwait(false);
            }
        }

        private async Task ObserveHandlerAsync(Task<IpcEnvelope> handlerTask, string command)
        {
            try
            {
                await handlerTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "Timed-out IPC command {Command} failed after cancellation", command);
            }
        }

        private async Task DisposeResourcesAsync()
        {
            if (Interlocked.Exchange(ref _resourcesDisposed, 1) == 1)
            {
                return;
            }

            if (_sendLoop is not null)
            {
                try
                {
                    await _sendLoop.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
                }
                catch (TimeoutException exception)
                {
                    _logger.LogWarning(exception, "Timed out stopping IPC sender for client {ClientId}", _id);
                    return;
                }
                catch (Exception exception)
                {
                    _logger.LogError(exception, "IPC sender failed while stopping client {ClientId}", _id);
                    return;
                }
            }

            DisposeStream(_reader, "reader");
            DisposeStream(_writer, "writer");
            _reader = null;
            _writer = null;
            _lifetime.Dispose();
        }

        private void DisposeStream(IDisposable? stream, string streamName)
        {
            try
            {
                stream?.Dispose();
            }
            catch (Exception exception) when (exception is IOException or ObjectDisposedException)
            {
                _logger.LogDebug(exception, "IPC client {Stream} was already closed", streamName);
            }
        }

        private async Task<string?> ReadBoundedLineAsync(StreamReader reader, CancellationToken cancellationToken)
        {
            var line = new StringBuilder();
            while (true)
            {
                if (_readBufferPosition == _readBufferLength)
                {
                    _readBufferLength = await reader.ReadAsync(_readBuffer.AsMemory(), cancellationToken)
                        .ConfigureAwait(false);
                    _readBufferPosition = 0;
                    if (_readBufferLength == 0)
                    {
                        return line.Length == 0 ? null : line.ToString();
                    }
                }

                var newline = Array.IndexOf(
                    _readBuffer,
                    '\n',
                    _readBufferPosition,
                    _readBufferLength - _readBufferPosition);
                var count = (newline >= 0 ? newline : _readBufferLength) - _readBufferPosition;
                if (line.Length > MaxMessageLength - count)
                {
                    throw new MessageTooLargeException();
                }

                line.Append(_readBuffer, _readBufferPosition, count);
                _readBufferPosition += count;
                if (newline < 0)
                {
                    continue;
                }

                _readBufferPosition++;
                if (line.Length > 0 && line[^1] == '\r')
                {
                    line.Length--;
                }

                return line.ToString();
            }
        }

        public void Close()
        {
            if (Interlocked.Exchange(ref _closedFlag, 1) == 1)
            {
                return;
            }

            _outgoing.Writer.TryComplete();
            try
            {
                _lifetime.Cancel();
            }
            catch (ObjectDisposedException)
            {
            }

            try
            {
                _pipe.Dispose();
            }
            catch (IOException)
            {
            }

            _closed(_id);
        }

        private sealed record OutgoingMessage(string Line, TaskCompletionSource? Completion);

        private sealed class MessageTooLargeException : IOException
        {
        }
    }
}
