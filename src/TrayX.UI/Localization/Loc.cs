using System.ComponentModel;

namespace TrayX.UI.Localization;

public sealed class Loc : INotifyPropertyChanged
{
    private string _language = "ru";

    public static Loc Instance { get; } = new();

    public event PropertyChangedEventHandler? PropertyChanged;

    public event EventHandler? LanguageChanged;

    public string Language => _language;

    public System.Globalization.CultureInfo Culture => _language == "en"
        ? System.Globalization.CultureInfo.GetCultureInfo("en-US")
        : System.Globalization.CultureInfo.GetCultureInfo("ru-RU");

    public string this[string key]
    {
        get
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                return string.Empty;
            }

            var map = _language == "en" ? StringsEn.Map : StringsRu.Map;
            if (map.TryGetValue(key, out var value))
            {
                return value;
            }

            return StringsRu.Map.TryGetValue(key, out var fallback) ? fallback : key;
        }
    }

    public void SetLanguage(string? language)
    {
        var normalized = string.Equals(language, "en", StringComparison.OrdinalIgnoreCase) ? "en" : "ru";
        if (_language == normalized)
        {
            return;
        }

        _language = normalized;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("Item[]"));
        LanguageChanged?.Invoke(this, EventArgs.Empty);
    }

    public static string T(string key) => Instance[key];
}
