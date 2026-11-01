namespace TrayX.Contracts;

public sealed record TrayAppRecoveryRecord(
    long WindowHandle,
    int ProcessId,
    long ProcessStartTimeUtcTicks,
    string WindowClass,
    string WindowTitle,
    int WindowShowCommand,
    DateTimeOffset PreparedAtUtc)
{
    public bool IsAutomatic { get; init; }
}
