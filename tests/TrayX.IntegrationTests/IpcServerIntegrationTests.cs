using System.IO.Pipes;
using System.Text;
using Microsoft.Extensions.Logging.Abstractions;
using TrayX.Contracts;
using TrayX.Core.Ipc;

namespace TrayX.IntegrationTests;

public sealed class IpcServerIntegrationTests
{
    [Fact]
    public async Task PublishAsync_WritesEventBeforeCompleting()
    {
        var pipeName = $"TrayX-test-{Guid.NewGuid():N}";
        await using var server = new IpcServer(pipeName, NullLogger<IpcServer>.Instance);
        server.ConfigureHandler((request, _) =>
            Task.FromResult(IpcEnvelope.Response(request.Id!.Value)));
        server.Start();

        await using var client = new NamedPipeClientStream(
            ".",
            pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);
        await client.ConnectAsync(5000).WaitAsync(TimeSpan.FromSeconds(5));
        using var reader = new StreamReader(client, new UTF8Encoding(false), false, 4096, leaveOpen: true);

        await WaitForClientConnectionAsync(server).WaitAsync(TimeSpan.FromSeconds(5));
        var receivedEvent = reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5));
        await server.PublishAsync("integration.event", new { value = 42 }).WaitAsync(TimeSpan.FromSeconds(5));

        var line = await receivedEvent;
        var envelope = IpcJson.Deserialize(line!);
        Assert.Equal(IpcMessageTypes.Event, envelope?.Type);
        Assert.Equal("integration.event", envelope?.Name);
        Assert.Equal(42, envelope?.Payload?.GetProperty("value").GetInt32());
    }

    [Fact]
    public async Task PublishAsync_PreservesEventOrderForAConnectedClient()
    {
        var pipeName = $"TrayX-test-{Guid.NewGuid():N}";
        await using var server = new IpcServer(pipeName, NullLogger<IpcServer>.Instance);
        server.ConfigureHandler((request, _) =>
            Task.FromResult(IpcEnvelope.Response(request.Id!.Value)));
        server.Start();

        await using var client = new NamedPipeClientStream(
            ".",
            pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);
        await client.ConnectAsync(5000).WaitAsync(TimeSpan.FromSeconds(5));
        using var reader = new StreamReader(client, new UTF8Encoding(false), false, 4096, leaveOpen: true);
        await WaitForClientConnectionAsync(server).WaitAsync(TimeSpan.FromSeconds(5));

        for (var index = 0; index < 32; index++)
        {
            server.Publish("integration.ordered", new { value = index });
        }

        for (var index = 0; index < 32; index++)
        {
            var line = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5));
            var envelope = IpcJson.Deserialize(line!);
            Assert.Equal("integration.ordered", envelope?.Name);
            Assert.Equal(index, envelope?.Payload?.GetProperty("value").GetInt32());
        }
    }

    private static async Task WaitForClientConnectionAsync(IpcServer server)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (server.ClientCount == 0)
        {
            await Task.Delay(10, timeout.Token);
        }
    }

    [Fact]
    public async Task OversizedMessage_ClosesClientBeforeDeserializingThePayload()
    {
        var pipeName = $"TrayX-test-{Guid.NewGuid():N}";
        await using var server = new IpcServer(pipeName, NullLogger<IpcServer>.Instance);
        server.ConfigureHandler((request, _) =>
            Task.FromResult(IpcEnvelope.Response(request.Id!.Value)));
        server.Start();

        await using var client = new NamedPipeClientStream(
            ".",
            pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);
        await client.ConnectAsync(5000);
        using var writer = new StreamWriter(client, new UTF8Encoding(false), 4096, leaveOpen: true)
        {
            AutoFlush = true
        };
        using var reader = new StreamReader(client, new UTF8Encoding(false), false, 4096, leaveOpen: true);

        await writer.WriteLineAsync(new string('x', 1_048_577));

        Assert.Null(await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task StopAsync_ClosesConnectedClientsAndCanBeAwaitedMoreThanOnce()
    {
        var pipeName = $"TrayX-test-{Guid.NewGuid():N}";
        await using var server = new IpcServer(pipeName, NullLogger<IpcServer>.Instance);
        server.ConfigureHandler((request, _) =>
            Task.FromResult(IpcEnvelope.Response(request.Id!.Value)));
        server.Start();

        await using var client = new NamedPipeClientStream(
            ".",
            pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);
        await client.ConnectAsync(5000).WaitAsync(TimeSpan.FromSeconds(5));
        using var reader = new StreamReader(client, new UTF8Encoding(false), false, 4096, leaveOpen: true);
        await WaitForClientConnectionAsync(server).WaitAsync(TimeSpan.FromSeconds(5));

        await Task.WhenAll(server.StopAsync(), server.StopAsync()).WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Null(await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(0, server.ClientCount);
    }

    [Fact]
    public async Task HandlerTimeout_CancelsCommandReturnsFailureAndClosesClient()
    {
        var pipeName = $"TrayX-test-{Guid.NewGuid():N}";
        var handlerCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var server = new IpcServer(
            pipeName,
            NullLogger<IpcServer>.Instance,
            TimeSpan.FromMilliseconds(100));
        server.ConfigureHandler(async (request, cancellationToken) =>
        {
            try
            {
                await Task.Delay(Timeout.Infinite, cancellationToken);
                return IpcEnvelope.Response(request.Id!.Value);
            }
            catch (OperationCanceledException)
            {
                handlerCancelled.TrySetResult();
                throw;
            }
        });
        server.Start();

        await using var client = new NamedPipeClientStream(
            ".",
            pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);
        await client.ConnectAsync(5000).WaitAsync(TimeSpan.FromSeconds(5));
        using var reader = new StreamReader(client, new UTF8Encoding(false), false, 4096, leaveOpen: true);
        await WaitForClientConnectionAsync(server).WaitAsync(TimeSpan.FromSeconds(5));

        var requestId = Guid.NewGuid();
        var requestLine = Encoding.UTF8.GetBytes(
            IpcJson.Serialize(IpcEnvelope.Request(requestId, "integration.hang")) + "\n");
        await client.WriteAsync(requestLine);

        var line = await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5));
        var response = IpcJson.Deserialize(line!);
        Assert.Equal(requestId, response?.Id);
        Assert.Equal(IpcMessageTypes.Response, response?.Type);
        Assert.False(response?.Success);
        Assert.Contains("ожидания", response?.Error, StringComparison.OrdinalIgnoreCase);
        await handlerCancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Null(await reader.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(5)));
    }
}
