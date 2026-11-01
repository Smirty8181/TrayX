using TrayX.UI.Localization;

namespace TrayX.UI.Services;

public static class UiFormat
{
    public static string Bytes(long bytes)
    {
        var units = Loc.Instance.Language == "en"
            ? new[] { "B", "KB", "MB", "GB", "TB" }
            : new[] { "Б", "КБ", "МБ", "ГБ", "ТБ" };

        var value = (double)Math.Max(0, bytes);
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0
            ? $"{value:0} {units[unit]}"
            : $"{value:0.#} {units[unit]}";
    }

    public static string Rate(double bytesPerSecond) =>
        $"{Bytes((long)Math.Max(0, bytesPerSecond))}/s";

    public static string Uptime(long seconds)
    {
        if (seconds <= 0)
        {
            return "—";
        }

        var span = TimeSpan.FromSeconds(seconds);
        return string.Format(
            Loc.T("System_UptimeFormat"),
            (int)span.TotalDays,
            span.Hours,
            span.Minutes);
    }
}
