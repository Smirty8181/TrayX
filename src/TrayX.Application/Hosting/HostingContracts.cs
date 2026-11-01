namespace TrayX.Application.Hosting;

public interface IClientEvents
{
    int ClientCount { get; }

    void Publish(string name, object? payload = null);
}

public interface IUiLauncher
{
    void Open(string page);
}

public interface ICoreControl
{
    void RequestShutdown();
}
