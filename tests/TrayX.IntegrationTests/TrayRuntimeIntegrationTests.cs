using TrayX.Contracts;
using TrayX.Windows.Shell;

namespace TrayX.IntegrationTests;

/// <summary>
/// Проверка только читающей части настоящего трея: тесты ничего не кликают
/// и не открывают область скрытых значков, чтобы не мешать работе за компьютером.
/// </summary>
public sealed class TrayRuntimeIntegrationTests : IDisposable
{
    private readonly TrayLiveService _live = new();
    private readonly TrayRuntimeService _runtime;

    public TrayRuntimeIntegrationTests()
    {
        _live.Start();
        _runtime = new TrayRuntimeService(_live, new TrayIconSettingsService());
    }

    [Fact]
    public void Scan_FindsIconsOnTheTaskbar()
    {
        var icons = _live.Scan();

        Assert.NotEmpty(icons);
        Assert.Contains(icons, icon => icon.OnBar);
        Assert.Equal(icons.Count, icons.Select(icon => icon.Id).Distinct().Count());
        Assert.All(icons, icon => Assert.StartsWith("live:", icon.Id, StringComparison.Ordinal));
        Assert.All(icons, icon => Assert.True(icon.Width > 0 && icon.Height > 0));
    }

    [Fact]
    public void Scan_IdsIgnoreVolatileNamesForStableLocations()
    {
        var first = _live.Scan()
            .Where(icon => icon.OnBar)
            .GroupBy(StableLocation)
            .ToDictionary(group => group.Key, group => group.First());
        Thread.Sleep(4000);
        var second = _live.Scan()
            .Where(icon => icon.OnBar)
            .GroupBy(StableLocation)
            .ToDictionary(group => group.Key, group => group.First());

        var repeatedLocations = first.Keys.Intersect(second.Keys).ToArray();
        Assert.NotEmpty(repeatedLocations);
        foreach (var location in repeatedLocations)
        {
            Assert.Equal(first[location].Id, second[location].Id);
        }
    }

    private static string StableLocation(TrayLiveIcon icon) =>
        $"{icon.MonitorIndex}|{icon.ClassName}|{icon.Left + icon.Width / 2}x{icon.Top + icon.Height / 2}";

    [Fact]
    public void Snapshot_WithoutLive_FallsBackToRegistry()
    {
        var snapshot = _runtime.Snapshot(includeLive: false);

        Assert.False(snapshot.LiveAvailable);
        Assert.Equal(TrayNotices.LiveDisabled, snapshot.Notice);
        Assert.DoesNotContain(snapshot.Icons, icon => icon.OnBar);
        Assert.All(snapshot.Icons, icon => Assert.StartsWith("reg:", icon.Id, StringComparison.Ordinal));
    }

    [Fact]
    public void Snapshot_MergesTaskbarAndRegistry()
    {
        var snapshot = _runtime.Snapshot(includeLive: true);

        Assert.True(snapshot.LiveAvailable);
        Assert.NotEmpty(snapshot.Icons);
        Assert.Contains(snapshot.Icons, icon => icon.OnBar && icon.MonitorIndex >= 0);
        Assert.Equal(snapshot.Icons.Count, snapshot.Icons.Select(icon => icon.Id).Distinct().Count());
    }

    [Fact]
    public void Snapshot_OrdersVisibleIconsFirst()
    {
        var snapshot = _runtime.Snapshot(includeLive: true);

        Assert.Equal(
            snapshot.Icons.Select(icon => icon.OnBar).OrderByDescending(value => value).ToArray(),
            snapshot.Icons.Select(icon => icon.OnBar).ToArray());
    }

    public void Dispose() => _live.Dispose();
}
