using TrayX.Modules.TrayManager;

namespace TrayX.IntegrationTests;

public sealed class TraySnapshotSignatureTrackerTests
{
    [Fact]
    public void TryUpdate_RejectsOlderSnapshotsThatCompleteOutOfOrder()
    {
        var tracker = new TraySnapshotSignatureTracker();

        Assert.True(tracker.TryUpdate(2, "newer"));
        Assert.False(tracker.TryUpdate(1, "older"));
        Assert.False(tracker.TryUpdate(3, "newer"));
        Assert.True(tracker.TryUpdate(4, "latest"));
    }

    [Fact]
    public void TryUpdate_ReportsChangedSignaturesOnlyOnceAcrossConcurrentScans()
    {
        var tracker = new TraySnapshotSignatureTracker();
        Assert.True(tracker.TryUpdate(1, "initial"));

        var results = Enumerable.Range(2, 32)
            .AsParallel()
            .Select(sequence => tracker.TryUpdate(sequence, "same"))
            .ToArray();

        Assert.Single(results, changed => changed);
    }
}
