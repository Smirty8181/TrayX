using System.Text.RegularExpressions;

namespace TrayX.Contracts;

public sealed record TrayAppWindowInfo(
    string ProcessName,
    string Title,
    string WindowClass);

public static class TrayAppRuleMatcher
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromMilliseconds(100);

    public static TrayAppRuleSettings ParseLegacyRule(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        var value = line.Trim();
        if (value.Length == 0)
        {
            throw new FormatException("Правило не может быть пустым.");
        }

        if (value.Length > 512)
        {
            throw new FormatException("Правило не может быть длиннее 512 символов.");
        }

        if (value.Contains('\r') || value.Contains('\n'))
        {
            throw new FormatException("Каждое правило должно находиться на отдельной строке.");
        }

        if (value.StartsWith("regex:", StringComparison.OrdinalIgnoreCase))
        {
            var pattern = value["regex:".Length..].Trim();
            if (pattern.Length == 0)
            {
                throw new FormatException("После regex: необходимо указать выражение.");
            }

            ValidateRegex(pattern);
            return ValidateRule(new TrayAppRuleSettings
            {
                Name = pattern,
                WindowTitle = pattern,
                MatchMode = TrayAppMatchMode.WindowTitleRegex
            });
        }

        if (value.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            if (value.Contains(Path.DirectorySeparatorChar) ||
                value.Contains(Path.AltDirectorySeparatorChar) ||
                value.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                throw new FormatException("Укажите имя процесса, например app.exe, без пути.");
            }

            return ValidateRule(new TrayAppRuleSettings
            {
                Name = value,
                ProcessName = value,
                MatchMode = TrayAppMatchMode.Process
            });
        }

        return ValidateRule(new TrayAppRuleSettings
        {
            Name = value,
            WindowTitle = value,
            MatchMode = TrayAppMatchMode.WindowTitle
        });
    }

    public static TrayAppRuleSettings ValidateRule(TrayAppRuleSettings rule)
    {
        ArgumentNullException.ThrowIfNull(rule);
        if (rule.Id == Guid.Empty ||
            !Enum.IsDefined(rule.MatchMode) ||
            string.IsNullOrWhiteSpace(rule.Name) ||
            (rule.Name ?? string.Empty).Length > 128 ||
            (rule.ProcessName ?? string.Empty).Length > 260 ||
            (rule.WindowTitle ?? string.Empty).Length > 512 ||
            (rule.WindowClass ?? string.Empty).Length > 256)
        {
            throw new FormatException("Правило содержит пустые или слишком длинные поля.");
        }

        switch (rule.MatchMode)
        {
            case TrayAppMatchMode.Process:
                if (string.IsNullOrWhiteSpace(rule.ProcessName) ||
                    rule.ProcessName.Contains(Path.DirectorySeparatorChar) ||
                    rule.ProcessName.Contains(Path.AltDirectorySeparatorChar))
                {
                    throw new FormatException("Для правила процесса укажите имя executable без пути.");
                }

                break;
            case TrayAppMatchMode.WindowTitle:
                if (string.IsNullOrWhiteSpace(rule.WindowTitle))
                {
                    throw new FormatException("Для правила заголовка укажите текст.");
                }

                break;
            case TrayAppMatchMode.WindowTitleRegex:
                if (string.IsNullOrWhiteSpace(rule.WindowTitle))
                {
                    throw new FormatException("Для regex-правила укажите выражение.");
                }

                ValidateRegex(rule.WindowTitle);
                break;
            case TrayAppMatchMode.WindowClass:
                if (string.IsNullOrWhiteSpace(rule.WindowClass))
                {
                    throw new FormatException("Для правила класса укажите имя класса окна.");
                }

                break;
            default:
                throw new FormatException("Неизвестный тип правила.");
        }

        return rule with
        {
            Name = (rule.Name ?? string.Empty).Trim(),
            ProcessName = (rule.ProcessName ?? string.Empty).Trim(),
            WindowTitle = (rule.WindowTitle ?? string.Empty).Trim(),
            WindowClass = (rule.WindowClass ?? string.Empty).Trim(),
            Priority = Math.Clamp(rule.Priority, -1000, 1000)
        };
    }

    public static IReadOnlyList<TrayAppRuleSettings> ParseLegacyRules(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var rules = new List<TrayAppRuleSettings>();
        foreach (var line in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#'))
            {
                continue;
            }

            rules.Add(ValidateRule(ParseLegacyRule(trimmed)));
        }

        return rules;
    }

    public static string ExportLegacyRules(IEnumerable<TrayAppRuleSettings> rules)
    {
        ArgumentNullException.ThrowIfNull(rules);
        var lines = new List<string>();
        foreach (var rule in rules.Where(rule => rule.IsEnabled))
        {
            if (rule.IsExclusion ||
                rule.Priority != 0 ||
                !string.IsNullOrWhiteSpace(rule.WindowClass) ||
                (rule.MatchMode == TrayAppMatchMode.Process &&
                 !string.IsNullOrWhiteSpace(rule.WindowTitle)) ||
                (rule.MatchMode != TrayAppMatchMode.Process &&
                 !string.IsNullOrWhiteSpace(rule.ProcessName)))
            {
                throw new InvalidOperationException(
                    "Это правило содержит условия, несовместимые с текстовым форматом Trayy.");
            }

            lines.Add(rule.MatchMode switch
            {
                TrayAppMatchMode.Process when string.IsNullOrWhiteSpace(rule.ProcessName) =>
                    throw new InvalidOperationException("Правило процесса не содержит имя процесса."),
                TrayAppMatchMode.Process => rule.ProcessName,
                TrayAppMatchMode.WindowTitle when string.IsNullOrWhiteSpace(rule.WindowTitle) =>
                    throw new InvalidOperationException("Правило заголовка не содержит текста."),
                TrayAppMatchMode.WindowTitle => rule.WindowTitle,
                TrayAppMatchMode.WindowTitleRegex when string.IsNullOrWhiteSpace(rule.WindowTitle) =>
                    throw new InvalidOperationException("Правило regex не содержит выражения."),
                TrayAppMatchMode.WindowTitleRegex => $"regex:{rule.WindowTitle}",
                _ => throw new InvalidOperationException(
                    "Это правило содержит условия, несовместимые с текстовым форматом Trayy.")
            });
        }

        return string.Join(Environment.NewLine, lines);
    }

    public static TrayAppRuleSettings? FindMatch(
        IEnumerable<TrayAppRuleSettings> rules,
        TrayAppWindowInfo window,
        Action<RegexMatchTimeoutException>? onRegexTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(rules);
        ArgumentNullException.ThrowIfNull(window);

        return rules
            .Where(rule => rule.IsEnabled && Matches(rule, window, onRegexTimeout))
            .OrderByDescending(rule => rule.Priority)
            .ThenByDescending(rule => rule.IsExclusion)
            .ThenBy(rule => rule.Id)
            .FirstOrDefault();
    }

    public static bool Matches(
        TrayAppRuleSettings rule,
        TrayAppWindowInfo window,
        Action<RegexMatchTimeoutException>? onRegexTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentNullException.ThrowIfNull(window);

        if (!string.IsNullOrWhiteSpace(rule.ProcessName) &&
            !string.Equals(
                Path.GetFileName(rule.ProcessName),
                Path.GetFileName(window.ProcessName),
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!string.IsNullOrWhiteSpace(rule.WindowClass) &&
            !string.Equals(rule.WindowClass, window.WindowClass, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (rule.MatchMode == TrayAppMatchMode.Process &&
            !rule.MatchAllProcessWindows &&
            !string.IsNullOrWhiteSpace(rule.WindowTitle) &&
            !window.Title.Contains(rule.WindowTitle, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return rule.MatchMode switch
        {
            TrayAppMatchMode.Process =>
                !string.IsNullOrWhiteSpace(rule.ProcessName) &&
                string.Equals(
                    Path.GetFileName(rule.ProcessName),
                    Path.GetFileName(window.ProcessName),
                    StringComparison.OrdinalIgnoreCase) &&
                (rule.MatchAllProcessWindows ||
                 string.IsNullOrWhiteSpace(rule.WindowTitle) ||
                 window.Title.Contains(rule.WindowTitle, StringComparison.OrdinalIgnoreCase)),
            TrayAppMatchMode.WindowTitle =>
                !string.IsNullOrWhiteSpace(rule.WindowTitle) &&
                window.Title.Contains(rule.WindowTitle, StringComparison.OrdinalIgnoreCase),
            TrayAppMatchMode.WindowTitleRegex =>
                !string.IsNullOrWhiteSpace(rule.WindowTitle) &&
                IsRegexMatch(rule.WindowTitle, window.Title, onRegexTimeout),
            TrayAppMatchMode.WindowClass =>
                !string.IsNullOrWhiteSpace(rule.WindowClass) &&
                string.Equals(rule.WindowClass, window.WindowClass, StringComparison.OrdinalIgnoreCase),
            _ => false
        };
    }

    private static bool IsRegexMatch(
        string pattern,
        string value,
        Action<RegexMatchTimeoutException>? onRegexTimeout)
    {
        try
        {
            return Regex.IsMatch(
                value,
                pattern,
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                RegexTimeout);
        }
        catch (RegexMatchTimeoutException exception)
        {
            onRegexTimeout?.Invoke(exception);
            return false;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static void ValidateRegex(string pattern)
    {
        try
        {
            _ = new Regex(
                pattern,
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
                RegexTimeout);
        }
        catch (ArgumentException exception)
        {
            throw new FormatException("Регулярное выражение имеет неверный синтаксис.", exception);
        }
    }
}
