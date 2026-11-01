namespace TrayX.Contracts;

public enum TrayAppMatchMode
{
    Process,
    WindowTitle,
    WindowTitleRegex,
    WindowClass
}

public sealed record TrayAppRuleSettings
{
    public Guid Id { get; init; } = Guid.NewGuid();

    public string Name { get; init; } = string.Empty;

    public string ProcessName { get; init; } = string.Empty;

    public string WindowTitle { get; init; } = string.Empty;

    public string WindowClass { get; init; } = string.Empty;

    public TrayAppMatchMode MatchMode { get; init; }

    public bool MatchAllProcessWindows { get; init; }

    public bool AutoHide { get; init; }

    public bool IsExclusion { get; init; }

    public bool IsEnabled { get; init; } = true;

    public int Priority { get; init; }
}
