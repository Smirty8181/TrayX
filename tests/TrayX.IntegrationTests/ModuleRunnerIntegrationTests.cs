using Microsoft.Extensions.Logging.Abstractions;
using TrayX.Application.Modules;
using TrayX.Core.Hosting;

namespace TrayX.IntegrationTests;

public sealed class ModuleRunnerIntegrationTests
{
    [Fact]
    public async Task StopAllAsync_ContinuesAfterFailureAndKeepsFailedModuleRunning()
    {
        var first = new TestModule("first");
        var failed = new TestModule("failed")
        {
            Stop = _ => throw new InvalidOperationException("stop failed")
        };
        var notStarted = new TestModule("not-started")
        {
            Start = _ => throw new InvalidOperationException("start failed")
        };
        var runner = new ModuleRunner(
            [first, failed, notStarted],
            NullLogger<ModuleRunner>.Instance);
        await runner.StartAllAsync(CancellationToken.None);

        await runner.StopAllAsync(CancellationToken.None);

        Assert.Equal(1, failed.StopCount);
        Assert.Equal(1, first.StopCount);
        Assert.Equal(0, notStarted.StopCount);
        Assert.False(runner.IsRunning("first"));
        Assert.True(runner.IsRunning("failed"));

        var failedStatus = Assert.Single(runner.GetStatuses(), status => status.Id == "failed");
        Assert.True(failedStatus.IsRunning);
        Assert.Equal("stop failed", failedStatus.Details);
    }

    [Fact]
    public async Task StopAllAsync_ContinuesAfterCancellation()
    {
        var first = new TestModule("first");
        var cancelled = new TestModule("cancelled")
        {
            Stop = _ => throw new OperationCanceledException("deadline reached")
        };
        var runner = new ModuleRunner(
            [first, cancelled],
            NullLogger<ModuleRunner>.Instance);

        await runner.StartAllAsync(CancellationToken.None);
        await runner.StopAllAsync(new CancellationToken(canceled: true));

        Assert.Equal(1, cancelled.StopCount);
        Assert.Equal(1, first.StopCount);
        Assert.True(runner.IsRunning("cancelled"));
        Assert.False(runner.IsRunning("first"));
    }

    [Fact]
    public async Task StartAllAsync_RollsBackStartedModulesWhenStartupIsCancelled()
    {
        var first = new TestModule("first");
        var cancelled = new TestModule("cancelled")
        {
            Start = _ => throw new OperationCanceledException("startup cancelled")
        };
        var runner = new ModuleRunner(
            [first, cancelled],
            NullLogger<ModuleRunner>.Instance);

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => runner.StartAllAsync(CancellationToken.None));

        Assert.Equal(1, first.StopCount);
        Assert.False(runner.IsRunning("first"));
        Assert.False(runner.IsRunning("cancelled"));
    }

    [Fact]
    public async Task StartAllAsync_DoesNotStartAlreadyRunningModulesTwice()
    {
        var module = new TestModule("module");
        var runner = new ModuleRunner([module], NullLogger<ModuleRunner>.Instance);

        await runner.StartAllAsync(CancellationToken.None);
        await runner.StartAllAsync(CancellationToken.None);

        Assert.Equal(1, module.StartCount);
        Assert.True(runner.IsRunning("module"));
    }

    private sealed class TestModule(string id) : ITrayXModule
    {
        public Func<CancellationToken, Task> Start { get; set; } = _ => Task.CompletedTask;

        public Func<CancellationToken, Task> Stop { get; set; } = _ => Task.CompletedTask;

        public int StopCount { get; private set; }

        public int StartCount { get; private set; }

        public string Id => id;

        public string DisplayName => id;

        public bool IsAvailable => true;

        public Task StartAsync(CancellationToken cancellationToken)
        {
            StartCount++;
            return Start(cancellationToken);
        }

        public Task StopAsync(CancellationToken cancellationToken)
        {
            StopCount++;
            return Stop(cancellationToken);
        }
    }
}
