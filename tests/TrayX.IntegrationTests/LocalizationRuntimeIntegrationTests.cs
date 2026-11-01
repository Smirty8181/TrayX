using TrayX.UI.Localization;

namespace TrayX.IntegrationTests;

public sealed class LocalizationRuntimeIntegrationTests
{
    [Fact]
    public void LanguageSelection_ReturnsValuesFromTheRuntimeDictionaries()
    {
        var localization = Loc.Instance;
        var originalLanguage = localization.Language;
        try
        {
            Assert.Equal(
                StringsRu.Map.Keys.OrderBy(key => key, StringComparer.Ordinal),
                StringsEn.Map.Keys.OrderBy(key => key, StringComparer.Ordinal));

            localization.SetLanguage("ru");
            foreach (var entry in StringsRu.Map)
            {
                Assert.Equal(entry.Value, Loc.T(entry.Key));
            }

            localization.SetLanguage("en");
            foreach (var entry in StringsEn.Map)
            {
                Assert.Equal(entry.Value, Loc.T(entry.Key));
            }

            Assert.Equal(string.Empty, Loc.T(" "));
            Assert.Equal("Missing_Localization_Key", Loc.T("Missing_Localization_Key"));
        }
        finally
        {
            localization.SetLanguage(originalLanguage);
        }
    }
}
