namespace TrayX.Application.Configuration;

public sealed class TrayXPaths
{
    public const string ProductFolderName = "TrayX";

    public TrayXPaths(string rootDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        RootDirectory = rootDirectory;
    }

    public string RootDirectory { get; }

    public string LogsDirectory => Path.Combine(RootDirectory, "logs");

    public string CacheDirectory => Path.Combine(RootDirectory, "cache");

    public string DatabaseFile => Path.Combine(RootDirectory, "trayx.db");

    public string ConfigurationFile => Path.Combine(RootDirectory, "config.json");

    public string TrayAppRecoveryFile => Path.Combine(RootDirectory, "tray-app-recovery.json");

    public static TrayXPaths ForCurrentUser()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return new TrayXPaths(Path.Combine(appData, ProductFolderName));
    }

    public void EnsureCreated()
    {
        Directory.CreateDirectory(RootDirectory);
        Directory.CreateDirectory(LogsDirectory);
        Directory.CreateDirectory(CacheDirectory);
    }
}
