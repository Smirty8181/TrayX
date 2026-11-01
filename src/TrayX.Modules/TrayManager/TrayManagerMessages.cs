using System.Globalization;

namespace TrayX.Modules.TrayManager;

internal enum TrayManagerMessage
{
    ExplorerRestarted,
    OverflowFailed,
    RecoverySummary,
    RecoveryFailed,
    WindowRestored,
    WindowRestoreFailed,
    AudioMuted,
    AudioUnmuted,
    AudioToggleFailed,
    ThemeChangedDark,
    ThemeChangedLight,
    ThemeChangeFailed
}

internal static class TrayManagerMessages
{
    public static (string Title, string Message) Format(
        string? language,
        TrayManagerMessage message,
        params object?[] arguments)
    {
        var english = string.Equals(language, "en", StringComparison.OrdinalIgnoreCase);
        var template = (english, message) switch
        {
            (true, TrayManagerMessage.ExplorerRestarted) =>
                ("Explorer restarted", "Tray icon settings have been applied."),
            (false, TrayManagerMessage.ExplorerRestarted) =>
                ("Проводник перезапущен", "Настройки значков трея применены."),
            (true, TrayManagerMessage.OverflowFailed) =>
                ("Hidden notification area", "Explorer did not respond to the request."),
            (false, TrayManagerMessage.OverflowFailed) =>
                ("Область скрытых значков", "Проводник не ответил на запрос."),
            (true, TrayManagerMessage.RecoverySummary) =>
                ("Window recovery", "Restored: {0}. Remaining in recovery log: {1}."),
            (false, TrayManagerMessage.RecoverySummary) =>
                ("Восстановление окон", "Восстановлено: {0}. Осталось в журнале: {1}."),
            (true, TrayManagerMessage.RecoveryFailed) =>
                ("Window recovery failed", "See the TrayX log for details."),
            (false, TrayManagerMessage.RecoveryFailed) =>
                ("Не удалось восстановить окна", "Подробности записаны в журнал TrayX."),
            (true, TrayManagerMessage.WindowRestored) =>
                ("Window recovery", "Restored: {0}"),
            (false, TrayManagerMessage.WindowRestored) =>
                ("Восстановление окна", "Восстановлено: {0}"),
            (true, TrayManagerMessage.WindowRestoreFailed) =>
                ("Window recovery", "Could not restore: {0}"),
            (false, TrayManagerMessage.WindowRestoreFailed) =>
                ("Восстановление окна", "Не удалось восстановить: {0}"),
            (true, TrayManagerMessage.AudioMuted) =>
                ("Sound muted", "All speakers are muted."),
            (false, TrayManagerMessage.AudioMuted) =>
                ("Звук выключен", "Все динамики без звука."),
            (true, TrayManagerMessage.AudioUnmuted) =>
                ("Sound enabled", "Volume has been restored."),
            (false, TrayManagerMessage.AudioUnmuted) =>
                ("Звук включён", "Громкость восстановлена."),
            (true, TrayManagerMessage.AudioToggleFailed) =>
                ("Could not change sound", "See the TrayX log for details."),
            (false, TrayManagerMessage.AudioToggleFailed) =>
                ("Не удалось изменить звук", "Подробности записаны в журнал TrayX."),
            (true, TrayManagerMessage.ThemeChangedDark) =>
                ("Windows theme changed", "Dark theme enabled."),
            (false, TrayManagerMessage.ThemeChangedDark) =>
                ("Тема Windows изменена", "Включена тёмная тема."),
            (true, TrayManagerMessage.ThemeChangedLight) =>
                ("Windows theme changed", "Light theme enabled."),
            (false, TrayManagerMessage.ThemeChangedLight) =>
                ("Тема Windows изменена", "Включена светлая тема."),
            (true, TrayManagerMessage.ThemeChangeFailed) =>
                ("Could not change theme", "See the TrayX log for details."),
            (false, TrayManagerMessage.ThemeChangeFailed) =>
                ("Не удалось сменить тему", "Подробности записаны в журнал TrayX."),
            _ => throw new ArgumentOutOfRangeException(nameof(message), message, "Unknown tray notification.")
        };

        return (template.Item1, string.Format(CultureInfo.CurrentCulture, template.Item2, arguments));
    }
}
