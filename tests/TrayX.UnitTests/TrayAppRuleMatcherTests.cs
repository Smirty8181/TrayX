using TrayX.Contracts;
using System.Text.RegularExpressions;

namespace TrayX.UnitTests;

public sealed class TrayAppRuleMatcherTests
{
    [Theory]
    [InlineData("Obsidian.exe", TrayAppMatchMode.Process)]
    [InlineData("WhatsApp Web", TrayAppMatchMode.WindowTitle)]
    [InlineData("regex:Калькулятор.*", TrayAppMatchMode.WindowTitleRegex)]
    public void ParseLegacyRule_MapsTrayySyntax(string text, TrayAppMatchMode expectedMode)
    {
        var rule = TrayAppRuleMatcher.ParseLegacyRule(text);

        Assert.Equal(expectedMode, rule.MatchMode);
    }

    [Fact]
    public void ParseLegacyRules_SkipsCommentsAndBlankLines()
    {
        var rules = TrayAppRuleMatcher.ParseLegacyRules(
            "# legacy rules\r\n\r\nObsidian.exe\r\nWhatsApp Web");

        Assert.Equal(2, rules.Count);
    }

    [Theory]
    [InlineData("")]
    [InlineData("regex:")]
    [InlineData("regex:[")]
    [InlineData("folder\\app.exe")]
    public void ParseLegacyRule_RejectsInvalidInput(string text)
    {
        Assert.Throws<FormatException>(() => TrayAppRuleMatcher.ParseLegacyRule(text));
    }

    [Fact]
    public void ParseLegacyRule_RejectsOversizedRule()
    {
        Assert.Throws<FormatException>(
            () => TrayAppRuleMatcher.ParseLegacyRule(new string('x', 513)));
    }

    [Fact]
    public void FindMatch_UsesPriorityThenExclusion()
    {
        var normal = new TrayAppRuleSettings
        {
            Id = Guid.Parse("00000000-0000-0000-0000-000000000001"),
            ProcessName = "app.exe",
            MatchMode = TrayAppMatchMode.Process,
            Priority = 10
        };
        var excluded = normal with
        {
            Id = Guid.Parse("00000000-0000-0000-0000-000000000002"),
            IsExclusion = true
        };

        var result = TrayAppRuleMatcher.FindMatch(
            [normal, excluded],
            new TrayAppWindowInfo("APP.EXE", "Window", "MainWindow"));

        Assert.Same(excluded, result);
    }

    [Fact]
    public void ProcessRule_CanBeLimitedToMatchingWindowTitle()
    {
        var rule = new TrayAppRuleSettings
        {
            ProcessName = "browser.exe",
            WindowTitle = "TrayX",
            MatchMode = TrayAppMatchMode.Process
        };

        Assert.True(TrayAppRuleMatcher.Matches(
            rule,
            new TrayAppWindowInfo("browser.exe", "TrayX - settings", "BrowserWindow")));
        Assert.False(TrayAppRuleMatcher.Matches(
            rule,
            new TrayAppWindowInfo("browser.exe", "Mail", "BrowserWindow")));
        Assert.True(TrayAppRuleMatcher.Matches(
            rule with { MatchAllProcessWindows = true },
            new TrayAppWindowInfo("browser.exe", "Mail", "BrowserWindow")));
    }

    [Fact]
    public void FindMatch_IgnoresDisabledRulesAndMatchesCyrillicRegex()
    {
        var rule = TrayAppRuleMatcher.ParseLegacyRule("regex:Калькулятор.*") with
        {
            IsEnabled = false
        };
        var window = new TrayAppWindowInfo("ApplicationFrameHost.exe", "Калькулятор", "ApplicationFrameWindow");

        Assert.Null(TrayAppRuleMatcher.FindMatch([rule], window));
        Assert.True(TrayAppRuleMatcher.Matches(rule with { IsEnabled = true }, window));
    }

    [Fact]
    public void Matches_InvalidPersistedRegex_FailsClosed()
    {
        var rule = new TrayAppRuleSettings
        {
            Name = "Invalid regex",
            WindowTitle = "[",
            MatchMode = TrayAppMatchMode.WindowTitleRegex
        };

        Assert.False(TrayAppRuleMatcher.Matches(
            rule,
            new TrayAppWindowInfo("app.exe", "window", "MainWindow")));
    }

    [Fact]
    public void Matches_RegexTimeoutFailsClosedAndReportsTheTimeout()
    {
        var rule = new TrayAppRuleSettings
        {
            Name = "Slow regex",
            WindowTitle = "(a+)+$",
            MatchMode = TrayAppMatchMode.WindowTitleRegex
        };
        RegexMatchTimeoutException? reported = null;

        var result = TrayAppRuleMatcher.Matches(
            rule,
            new TrayAppWindowInfo("app.exe", new string('a', 100_000) + "!", "MainWindow"),
            exception => reported = exception);

        Assert.False(result);
        Assert.NotNull(reported);
    }

    [Fact]
    public void ExportLegacyRules_RejectsRulesNotRepresentableInLegacyFormat()
    {
        var rule = new TrayAppRuleSettings
        {
            ProcessName = "chrome.exe",
            WindowTitle = "App",
            MatchMode = TrayAppMatchMode.WindowTitle
        };

        Assert.Throws<InvalidOperationException>(
            () => TrayAppRuleMatcher.ExportLegacyRules([rule]));
    }

    [Fact]
    public void ValidateRule_RejectsNullFieldsFromMalformedJson()
    {
        var rule = new TrayAppRuleSettings
        {
            Name = null!,
            ProcessName = null!,
            WindowTitle = null!,
            WindowClass = null!
        };

        Assert.Throws<FormatException>(() => TrayAppRuleMatcher.ValidateRule(rule));
    }
}
