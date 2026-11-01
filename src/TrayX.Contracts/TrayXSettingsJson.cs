using System.Text.Json;

namespace TrayX.Contracts;

public static class TrayXSettingsJson
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public static string Serialize(TrayXSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return JsonSerializer.Serialize(settings.Normalize(), Options);
    }

    public static TrayXSettings Deserialize(string json)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(json);

        var settings = JsonSerializer.Deserialize<TrayXSettings>(json, Options)
            ?? throw new JsonException("Файл настроек не содержит объект TrayXSettings.");
        EnsureSupportedVersion(settings);

        return settings.Normalize();
    }

    public static void EnsureSupportedVersion(TrayXSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.ConfigVersion > TrayXSettings.CurrentConfigVersion)
        {
            throw new JsonException(
                $"Версия настроек {settings.ConfigVersion} новее поддерживаемой {TrayXSettings.CurrentConfigVersion}.");
        }
    }
}
