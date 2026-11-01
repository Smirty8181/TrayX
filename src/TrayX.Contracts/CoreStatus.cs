namespace TrayX.Contracts;

public sealed record CoreStatus(bool IsReady, string Version, DateTimeOffset StartedAt);
