namespace TrayX.Modules.TrayManager;

internal sealed class TraySnapshotSignatureTracker
{
    private readonly object _sync = new();
    private string _signature = string.Empty;
    private long _sequence = long.MinValue;

    public bool TryUpdate(long sequence, string signature)
    {
        ArgumentNullException.ThrowIfNull(signature);
        lock (_sync)
        {
            if (sequence <= _sequence)
            {
                return false;
            }

            var changed = !string.Equals(signature, _signature, StringComparison.Ordinal);
            _signature = signature;
            _sequence = sequence;
            return changed;
        }
    }
}
