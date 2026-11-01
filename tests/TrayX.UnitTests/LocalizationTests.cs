using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace TrayX.UnitTests;

/// <summary>
/// Словари строк и разметка лежат в WPF-проекте, который тесты не собирают,
/// поэтому ключи сравниваются по исходникам.
/// </summary>
public sealed class LocalizationTests
{
    private static readonly Regex Definition = new(
        @"\[""([A-Za-z0-9_]+)""\]\s*=\s*""((?:\\.|[^""])*)""",
        RegexOptions.Compiled);

    private static readonly Regex MarkupKey = new(
        @"\{loc:Loc\s+([A-Za-z][A-Za-z0-9_]*)",
        RegexOptions.Compiled);

    private static readonly Regex CodeKey = new(
        @"Loc\.T\(\s*""([A-Za-z][A-Za-z0-9_]*)""",
        RegexOptions.Compiled);

    private static readonly Regex Placeholder = new(
        @"\{\d+(?::[^}]*)?\}",
        RegexOptions.Compiled);

    [Fact]
    public void BothLanguages_DefineTheSameKeys()
    {
        var russian = ReadMap("StringsRu.cs");
        var english = ReadMap("StringsEn.cs");

        Assert.Equal(russian.Keys.OrderBy(key => key, StringComparer.Ordinal),
            english.Keys.OrderBy(key => key, StringComparer.Ordinal));
    }

    [Fact]
    public void BothLanguages_UseTheSamePlaceholders()
    {
        var russian = ReadMap("StringsRu.cs");
        var english = ReadMap("StringsEn.cs");

        foreach (var key in russian.Keys)
        {
            var expected = Placeholders(russian[key]);
            Assert.Equal(expected, Placeholders(english[key]));
        }
    }

    [Fact]
    public void NoValue_IsLeftEmpty()
    {
        foreach (var file in new[] { "StringsRu.cs", "StringsEn.cs" })
        {
            foreach (var pair in ReadMap(file))
            {
                Assert.False(string.IsNullOrWhiteSpace(pair.Value), $"{file}: пустая строка для {pair.Key}");
            }
        }
    }

    [Fact]
    public void Interface_ReferencesKnownKeys()
    {
        var russian = ReadMap("StringsRu.cs");
        var english = ReadMap("StringsEn.cs");
        var root = RepositoryRoot();

        var missing = new List<string>();
        var referenced = 0;
        foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "src", "TrayX.UI"), "*.*", SearchOption.AllDirectories)
                     .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")
                                    && !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")
                                    && (path.EndsWith(".xaml", StringComparison.OrdinalIgnoreCase)
                                        || path.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))))
        {
            if (Path.GetFileNameWithoutExtension(file).StartsWith("Strings", StringComparison.Ordinal))
            {
                continue;
            }

            var text = File.ReadAllText(file);
            var keys = MarkupKey.Matches(text).Select(match => match.Groups[1].Value)
                .Concat(CodeKey.Matches(text).Select(match => match.Groups[1].Value))
                .Distinct(StringComparer.Ordinal)
                .ToArray();
            referenced += keys.Length;

            foreach (var key in keys)
            {
                if (!russian.ContainsKey(key) || !english.ContainsKey(key))
                {
                    missing.Add($"{Path.GetRelativePath(root, file)} → {key}");
                }
            }
        }

        // Без найденных ссылок проверка прошла бы молча, поэтому сверяем и их количество.
        Assert.True(referenced > 200, $"Найдено только {referenced} ссылок на строки интерфейса.");
        Assert.Empty(missing);
    }

    [Fact]
    public void IconButtonStyles_UseToolTipsAsAutomationNames()
    {
        var path = Path.Combine(RepositoryRoot(), "src", "TrayX.UI", "Themes", "Controls.xaml");
        var document = XDocument.Load(path);
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

        foreach (var styleKey in new[] { "IconButton", "TitleBarButton" })
        {
            var style = document
                .Descendants(presentation + "Style")
                .Single(element => (string?)element.Attribute(xaml + "Key") == styleKey);
            var setter = style
                .Elements(presentation + "Setter")
                .Single(element => (string?)element.Attribute("Property") == "AutomationProperties.Name");
            var binding = setter.Element(presentation + "Setter.Value")?.Element(presentation + "Binding");

            Assert.NotNull(binding);
            Assert.Equal("{RelativeSource Self}", (string?)binding.Attribute("RelativeSource"));
            Assert.Equal("ToolTip", (string?)binding.Attribute("Path"));
        }
    }

    [Fact]
    public void IconButtons_OnEveryPage_ProvideAutomationNames()
    {
        var root = Path.Combine(RepositoryRoot(), "src", "TrayX.UI", "Views");
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

        foreach (var path in Directory.EnumerateFiles(root, "*.xaml"))
        {
            var document = XDocument.Load(path);
            foreach (var button in document.Descendants(presentation + "Button")
                         .Where(element => (string?)element.Attribute("Style") == "{StaticResource IconButton}"))
            {
                var hasTooltip = button.Attribute("ToolTip") is not null ||
                    button.Descendants(presentation + "Setter")
                        .Any(setter => (string?)setter.Attribute("Property") == "ToolTip");

                Assert.True(
                    hasTooltip,
                    $"{Path.GetFileName(path)} contains an icon button without an accessible name.");
            }
        }
    }

    [Fact]
    public void NotificationHistory_UsesAnAccessibleList()
    {
        var path = Path.Combine(RepositoryRoot(), "src", "TrayX.UI", "Views", "NotificationsPage.xaml");
        var document = XDocument.Load(path);
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

        var list = Assert.Single(document.Descendants(presentation + "ListBox"));

        Assert.Equal("{Binding Items}", (string?)list.Attribute("ItemsSource"));
        Assert.Equal("{loc:Loc Notifications_History}", (string?)list.Attribute("AutomationProperties.Name"));
        Assert.Equal("{StaticResource PlainListBox}", (string?)list.Attribute("Style"));
        Assert.NotNull(list.Element(presentation + "ListBox.ItemTemplate"));
        Assert.DoesNotContain(document.Descendants(presentation + "ItemsControl"),
            control => (string?)control.Attribute("ItemsSource") == "{Binding Items}");
    }

    [Fact]
    public void CustomControls_UseThemeAwareSliderStatesAndSingleSelectionTrigger()
    {
        var path = Path.Combine(RepositoryRoot(), "src", "TrayX.UI", "Themes", "Controls.xaml");
        var document = XDocument.Load(path);
        XNamespace presentation = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";

        var sliderStyle = document.Descendants(presentation + "Style")
            .Single(element => (string?)element.Attribute(xaml + "Key") == "NeonSlider");
        var sliderTemplate = sliderStyle.Elements(presentation + "Setter")
            .Single(element => (string?)element.Attribute("Property") == "Template")
            .Element(presentation + "Setter.Value")!
            .Element(presentation + "ControlTemplate")!;
        var thumbTemplate = sliderTemplate.Descendants(presentation + "Thumb")
            .Single()
            .Element(presentation + "Thumb.Template")!
            .Element(presentation + "ControlTemplate")!;
        var thumbTriggers = thumbTemplate.Element(presentation + "ControlTemplate.Triggers")!
            .Elements(presentation + "Trigger")
            .Select(trigger => (string?)trigger.Attribute("Property"))
            .ToArray();

        Assert.Contains("IsMouseOver", thumbTriggers);
        Assert.Contains("IsDragging", thumbTriggers);
        Assert.DoesNotContain(thumbTemplate.Descendants(presentation + "Ellipse"),
            ellipse => (string?)ellipse.Attribute("Fill") == "White");

        var segmentStyle = document.Descendants(presentation + "Style")
            .Single(element => (string?)element.Attribute(xaml + "Key") == "SegmentRadio");
        var segmentTemplate = segmentStyle.Elements(presentation + "Setter")
            .Single(element => (string?)element.Attribute("Property") == "Template")
            .Element(presentation + "Setter.Value")!
            .Element(presentation + "ControlTemplate")!;

        Assert.Single(
            segmentTemplate.Descendants(presentation + "Trigger"),
            trigger => (string?)trigger.Attribute("Property") == "IsChecked");
    }

    private static IReadOnlyDictionary<string, string> ReadMap(string fileName)
    {
        var path = Path.Combine(RepositoryRoot(), "src", "TrayX.UI", "Localization", fileName);
        var map = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (Match match in Definition.Matches(File.ReadAllText(path)))
        {
            map[match.Groups[1].Value] = match.Groups[2].Value;
        }

        Assert.NotEmpty(map);
        return map;
    }

    private static string[] Placeholders(string value) =>
        Placeholder.Matches(value).Select(match => match.Value).OrderBy(text => text, StringComparer.Ordinal).ToArray();

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "TrayX.sln")))
        {
            directory = directory.Parent;
        }

        Assert.NotNull(directory);
        return directory!.FullName;
    }
}
