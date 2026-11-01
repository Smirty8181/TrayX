namespace TrayX.Application.Modules;

public interface ITrayXModule
{
    string Id { get; }

    string DisplayName { get; }

    bool IsAvailable { get; }

    Task StartAsync(CancellationToken cancellationToken);

    Task StopAsync(CancellationToken cancellationToken);
}
