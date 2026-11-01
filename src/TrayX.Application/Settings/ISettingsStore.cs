using TrayX.Contracts;

namespace TrayX.Application.Settings;

public interface ISettingsStore
{
    TrayXSettings Current { get; }

    event EventHandler<TrayXSettings>? Changed;

    Task UpdateAsync(TrayXSettings settings, CancellationToken cancellationToken = default);

    Task UpdateAsync(
        Func<TrayXSettings, TrayXSettings> update,
        CancellationToken cancellationToken = default);

    Task ReloadAsync(CancellationToken cancellationToken = default);
}
