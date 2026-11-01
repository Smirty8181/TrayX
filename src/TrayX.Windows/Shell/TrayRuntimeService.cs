using System.IO;
using TrayX.Contracts;

namespace TrayX.Windows.Shell;

/// <summary>
/// Собирает полную картину трея: что реально видно на панели (UI Automation),
/// что лежит в реестре значков и чей процесс запущен.
/// </summary>
public sealed class TrayRuntimeService
{
    private const string LiveIdPrefix = "live:";
    private const string RegistryIdPrefix = "reg:";

    private readonly TrayLiveService _live;
    private readonly TrayIconSettingsService _registry;

    public TrayRuntimeService(TrayLiveService live, TrayIconSettingsService registry)
    {
        _live = live;
        _registry = registry;
    }

    /// <param name="includeLive">false — только реестр значков: UI Automation не опрашивается.</param>
    public TraySnapshotDto Snapshot(bool includeLive)
    {
        List<TrayLiveIcon> liveIcons;
        string? notice;
        var liveAvailable = includeLive;

        if (!includeLive)
        {
            liveIcons = [];
            notice = TrayNotices.LiveDisabled;
        }
        else
        {
            try
            {
                liveIcons = _live.Scan().ToList();
                notice = null;
            }
            catch (Exception exception) when (
                exception is TimeoutException or
                    InvalidOperationException or
                    System.Runtime.InteropServices.ExternalException)
            {
                liveIcons = [];
                liveAvailable = false;
                notice = TrayNotices.UiaUnavailable;
            }
        }

        var registered = _registry.GetIcons();
        var processes = ProcessProbe.ReadNames();
        var overflowOpen = liveIcons.Any(icon => !icon.OnBar);
        var chosen = MatchLiveIcons(liveIcons, registered);
        var icons = new List<TrayLiveIconDto>(liveIcons.Count + registered.Count);

        foreach (var icon in liveIcons)
        {
            chosen.TryGetValue(icon.Id, out var entry);
            var processId = FindProcessId(processes, entry?.ExecutablePath, icon.Name);

            icons.Add(new TrayLiveIconDto(
                icon.Id,
                string.IsNullOrWhiteSpace(icon.Name) ? entry?.Name ?? "Значок" : icon.Name,
                entry is not null || processId > 0 ? TrayIconKinds.Application : TrayIconKinds.System,
                icon.MonitorIndex,
                icon.OnBar,
                true,
                entry?.IsPromoted ?? false,
                processId > 0,
                processId,
                entry?.Name,
                entry?.ExecutablePath,
                entry?.Key,
                icon.Left,
                icon.Top,
                icon.Width,
                icon.Height));
        }

        var usedKeys = chosen.Values.Select(entry => entry.Key).ToHashSet(StringComparer.Ordinal);

        foreach (var entry in registered)
        {
            if (usedKeys.Contains(entry.Key))
            {
                continue;
            }

            var processId = FindProcessId(processes, entry.ExecutablePath, entry.Tooltip ?? entry.Name);
            var kind = processId > 0
                ? overflowOpen ? TrayIconKinds.Overflow : TrayIconKinds.Hidden
                : TrayIconKinds.Stale;

            icons.Add(new TrayLiveIconDto(
                RegistryIdPrefix + entry.Key,
                string.IsNullOrWhiteSpace(entry.Tooltip) ? entry.Name : entry.Tooltip,
                kind,
                -1,
                false,
                processId > 0,
                entry.IsPromoted,
                processId > 0,
                processId,
                entry.Name,
                entry.ExecutablePath,
                entry.Key,
                0,
                0,
                0,
                0));
        }

        return new TraySnapshotDto(
            DateTimeOffset.Now,
            liveAvailable,
            overflowOpen,
            notice,
            icons
                .OrderByDescending(icon => icon.OnBar)
                .ThenBy(icon => icon.MonitorIndex)
                .ThenBy(icon => icon.Top)
                .ThenBy(icon => icon.Left)
                .ToList());
    }

    public bool Activate(string id)
    {
        var liveId = ResolveLiveId(id, out var openedFlyout);
        if (liveId is null)
        {
            return false;
        }

        var result = _live.Activate(liveId);
        // Вызов из области скрытых значков сам закрывает всплывающую панель; если он её оставил — закрываем мы.
        if (openedFlyout && IsOverflowOpen)
        {
            _live.ToggleOverflow();
        }

        return result;
    }

    public bool ShowContextMenu(string id)
    {
        var liveId = ResolveLiveId(id, out _);
        return liveId is not null && _live.ShowContextMenu(liveId);
    }

    /// <summary>
    /// Возвращает true только если состояние области переполнения реально изменилось:
    /// проводник принимает Invoke и не показывает панель, когда фокус захвачен полноэкранным приложением.
    /// </summary>
    public bool ToggleOverflow()
    {
        var wasOpen = IsOverflowOpen;
        if (!_live.ToggleOverflow())
        {
            return false;
        }

        for (var attempt = 0; attempt < 6; attempt++)
        {
            Thread.Sleep(120);
            if (IsOverflowOpen != wasOpen)
            {
                return true;
            }
        }

        return false;
    }

    public bool IsOverflowOpen
    {
        get
        {
            try
            {
                return _live.Scan().Any(icon => !icon.OnBar);
            }
            catch (Exception exception) when (
                exception is TimeoutException or
                    InvalidOperationException or
                    ObjectDisposedException or
                    System.Runtime.InteropServices.ExternalException)
            {
                return false;
            }
        }
    }

    public string? IconImage(string key) => _registry.GetIconImage(key);

    private static Dictionary<string, TrayIconDto> MatchLiveIcons(
        IReadOnlyList<TrayLiveIcon> liveIcons,
        IReadOnlyList<TrayIconDto> registered)
    {
        var candidates = new List<(TrayLiveIcon Icon, TrayIconDto Entry, int Score)>();

        foreach (var icon in liveIcons)
        {
            foreach (var entry in registered)
            {
                var score = Score(icon.Name, entry);
                if (score > 0)
                {
                    candidates.Add((icon, entry, score));
                }
            }
        }

        var result = new Dictionary<string, TrayIconDto>(StringComparer.Ordinal);
        var usedKeys = new HashSet<string>(StringComparer.Ordinal);

        // Точные совпадения подсказок разбираем первыми, иначе общий префикс утащит не тот значок.
        foreach (var candidate in candidates.OrderByDescending(item => item.Score))
        {
            if (result.ContainsKey(candidate.Icon.Id) || !usedKeys.Add(candidate.Entry.Key))
            {
                continue;
            }

            result[candidate.Icon.Id] = candidate.Entry;
        }

        return result;
    }

    private static int Score(string liveName, TrayIconDto entry)
    {
        var name = Normalize(liveName);
        if (name.Length == 0)
        {
            return 0;
        }

        var tooltip = Normalize(entry.Tooltip);
        if (tooltip.Length >= 3)
        {
            if (name == tooltip)
            {
                return 6;
            }

            if (name.StartsWith(tooltip, StringComparison.Ordinal) ||
                tooltip.StartsWith(name, StringComparison.Ordinal))
            {
                return 5;
            }

            if (name.Contains(tooltip, StringComparison.Ordinal))
            {
                return 4;
            }
        }

        var file = Normalize(Path.GetFileNameWithoutExtension(entry.ExecutablePath));
        if (file.Length >= 4 && HasToken(name, file))
        {
            return 3;
        }

        var title = Normalize(entry.Name);
        if (title.Length >= 4 && HasToken(name, title))
        {
            return 3;
        }

        return 0;
    }

    private static int FindProcessId(
        IReadOnlyDictionary<string, int> processes,
        string? executable,
        string liveName)
    {
        if (!string.IsNullOrEmpty(executable))
        {
            var file = Path.GetFileNameWithoutExtension(executable);
            if (processes.TryGetValue(file, out var direct))
            {
                return direct;
            }
        }

        var name = Normalize(liveName);
        if (name.Length == 0)
        {
            return 0;
        }

        foreach (var (process, id) in processes)
        {
            var token = Normalize(process);
            if (token.Length >= 4 && HasToken(name, token))
            {
                return id;
            }
        }

        return 0;
    }

    private static bool HasToken(string text, string token)
    {
        var index = text.IndexOf(token, StringComparison.Ordinal);
        while (index >= 0)
        {
            var before = index == 0 || !char.IsLetterOrDigit(text[index - 1]);
            var next = index + token.Length;
            var after = next >= text.Length || !char.IsLetterOrDigit(text[next]);
            if (before && after)
            {
                return true;
            }

            index = text.IndexOf(token, index + 1, StringComparison.Ordinal);
        }

        return false;
    }

    private static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var builder = new System.Text.StringBuilder(value.Length);
        var lastWasSpace = true;

        foreach (var symbol in value)
        {
            if (symbol is '\u200F' or '\u200E' or '\r' or '\n' or '\t')
            {
                lastWasSpace = true;
                continue;
            }

            if (char.IsWhiteSpace(symbol))
            {
                if (!lastWasSpace)
                {
                    builder.Append(' ');
                    lastWasSpace = true;
                }

                continue;
            }

            builder.Append(char.ToLowerInvariant(symbol));
            lastWasSpace = false;
        }

        return builder.ToString().Trim();
    }

    private string? ResolveLiveId(string id, out bool openedFlyout)
    {
        openedFlyout = false;

        if (string.IsNullOrWhiteSpace(id))
        {
            return null;
        }

        if (id.StartsWith(LiveIdPrefix, StringComparison.Ordinal))
        {
            return id;
        }

        if (!id.StartsWith(RegistryIdPrefix, StringComparison.Ordinal))
        {
            return null;
        }

        var key = id[RegistryIdPrefix.Length..];
        var entry = _registry.GetIcons().FirstOrDefault(item => item.Key == key);
        if (entry is null)
        {
            return null;
        }

        if (!IsOverflowOpen)
        {
            // Скрытый значок живёт в области переполнения: без её открытия до него не достать.
            if (!ToggleOverflow())
            {
                return null;
            }

            openedFlyout = true;
        }

        for (var attempt = 0; attempt < 8; attempt++)
        {
            Thread.Sleep(150);
            var match = _live.Scan()
                .Select(icon => (Icon: icon, Score: Score(icon.Name, entry)))
                .Where(item => item.Score >= 4)
                .OrderByDescending(item => item.Score)
                .ThenByDescending(item => item.Icon.OnBar)
                .FirstOrDefault()
                .Icon;

            if (match is not null)
            {
                return match.Id;
            }
        }

        return null;
    }
}
