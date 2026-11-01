using TrayX.Contracts;

namespace TrayX.Application.TrayApps;

public interface ITrayAppRecoveryJournal
{
    Task<IReadOnlyList<TrayAppRecoveryRecord>> GetPendingAsync(
        CancellationToken cancellationToken = default);

    Task PrepareAsync(TrayAppRecoveryRecord record, CancellationToken cancellationToken = default);

    Task CompleteAsync(
        long windowHandle,
        int processId,
        long processStartTimeUtcTicks,
        CancellationToken cancellationToken = default);
}
