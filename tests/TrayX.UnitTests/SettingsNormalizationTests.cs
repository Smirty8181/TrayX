using TrayX.Contracts;
using System.Text.Json;

namespace TrayX.UnitTests;

public sealed class SettingsNormalizationTests
{
    [Fact]
    public void Default_ReadsTheRealTray()
    {
        var settings = TrayXSettings.Default;

        Assert.True(settings.TrayLiveEnabled);
        Assert.Equal(3000, settings.TrayLivePollMs);
    }

    [Fact]
    public void DeserializingLegacySettings_UsesCurrentConfigVersion()
    {
        var settings = JsonSerializer.Deserialize<TrayXSettings>("{}");

        Assert.NotNull(settings);
        Assert.Equal(TrayXSettings.CurrentConfigVersion, settings.ConfigVersion);
    }

    [Fact]
    public void DeserializingVersionOneSettings_AddsFavoriteDeviceSetting()
    {
        var settings = JsonSerializer.Deserialize<TrayXSettings>("{\"configVersion\":1}");

        Assert.NotNull(settings);
        Assert.Equal(TrayXSettings.CurrentConfigVersion, settings.Normalize().ConfigVersion);
        Assert.Empty(settings.FavoriteAudioDeviceIds);
    }

    [Fact]
    public void DeserializingVersionTwoSettings_UsesSystemClockFormat()
    {
        var settings = JsonSerializer.Deserialize<TrayXSettings>("{\"configVersion\":2}");

        Assert.NotNull(settings);
        Assert.Equal(TrayXSettings.CurrentConfigVersion, settings.Normalize().ConfigVersion);
        Assert.Equal("system", settings.Normalize().ClockFormat);
    }

    [Fact]
    public void DeserializingLegacySettings_InitializesTrayAppRules()
    {
        var settings = JsonSerializer.Deserialize<TrayXSettings>("{\"configVersion\":3}");

        Assert.NotNull(settings);
        Assert.Empty(settings.Normalize().TrayAppRules);
        Assert.Equal(TrayXSettings.CurrentConfigVersion, settings.Normalize().ConfigVersion);
    }

    [Fact]
    public void Normalize_ClampsAndDeduplicatesTrayAppRules()
    {
        var id = Guid.NewGuid();
        var settings = TrayXSettings.Default with
        {
            TrayAppRules =
            [
                new TrayAppRuleSettings
                {
                    Id = id,
                    Name = "  browser  ",
                    ProcessName = " browser.exe ",
                    Priority = int.MaxValue
                },
                new TrayAppRuleSettings
                {
                    Id = id,
                    Name = "duplicate"
                }
            ]
        };

        var rule = Assert.Single(settings.Normalize().TrayAppRules);
        Assert.Equal("browser", rule.Name);
        Assert.Equal("browser.exe", rule.ProcessName);
        Assert.Equal(1000, rule.Priority);
    }

    [Fact]
    public void Normalize_ReplacesUnknownTrayAppMatchMode()
    {
        var settings = TrayXSettings.Default with
        {
            TrayAppRules =
            [
                new TrayAppRuleSettings
                {
                    Name = "Unknown mode",
                    ProcessName = "app.exe",
                    MatchMode = (TrayAppMatchMode)int.MaxValue
                }
            ]
        };

        Assert.Equal(TrayAppMatchMode.Process, Assert.Single(settings.Normalize().TrayAppRules).MatchMode);
    }

    [Fact]
    public void TrayAppsAutoHide_IsDisabledByDefault()
    {
        Assert.False(TrayXSettings.Default.Normalize().TrayAppsAutoHideEnabled);
    }

    [Fact]
    public void VersionFourTrayRules_DoNotEnableAutomaticHiding()
    {
        var settings = JsonSerializer.Deserialize<TrayXSettings>(
            """{"ConfigVersion":4,"TrayAppRules":[{"Name":"Legacy","ProcessName":"legacy.exe","MatchMode":0}]}""");

        Assert.NotNull(settings);
        var normalized = settings.Normalize();
        Assert.False(normalized.TrayAppsAutoHideEnabled);
        Assert.False(Assert.Single(normalized.TrayAppRules).AutoHide);
    }

    [Fact]
    public void TrayAppRules_SurviveConfigurationSerialization()
    {
        var settings = TrayXSettings.Default with
        {
            TrayAppsAutoHideEnabled = true,
            TrayAppRules =
            [
                new TrayAppRuleSettings
                {
                    Name = "Калькулятор",
                    WindowTitle = "Калькулятор",
                    MatchMode = TrayAppMatchMode.WindowTitle,
                    Priority = 12,
                    IsExclusion = true,
                    AutoHide = true
                }
            ]
        };

        var restored = JsonSerializer.Deserialize<TrayXSettings>(JsonSerializer.Serialize(settings));

        Assert.NotNull(restored);
        var rule = Assert.Single(restored.Normalize().TrayAppRules);
        Assert.Equal("Калькулятор", rule.Name);
        Assert.Equal(TrayAppMatchMode.WindowTitle, rule.MatchMode);
        Assert.Equal(12, rule.Priority);
        Assert.True(rule.IsExclusion);
        Assert.True(rule.AutoHide);
        Assert.True(restored.TrayAppsAutoHideEnabled);
    }

    [Fact]
    public void Normalize_UpgradesUnversionedSettings()
    {
        var settings = TrayXSettings.Default with { ConfigVersion = 0 };

        Assert.Equal(TrayXSettings.CurrentConfigVersion, settings.Normalize().ConfigVersion);
    }

    [Theory]
    [InlineData(0, 1000)]
    [InlineData(250, 1000)]
    [InlineData(5000, 5000)]
    [InlineData(120000, 30000)]
    public void Normalize_ClampsLivePollInterval(int value, int expected)
    {
        var settings = TrayXSettings.Default with { TrayLivePollMs = value };

        Assert.Equal(expected, settings.Normalize().TrayLivePollMs);
    }

    [Fact]
    public void Normalize_KeepsTheLiveSwitchAndOtherFields()
    {
        var settings = TrayXSettings.Default with
        {
            TrayLiveEnabled = false,
            Language = "en",
            Accent = "#FF3399"
        };

        var normalized = settings.Normalize();

        Assert.False(normalized.TrayLiveEnabled);
        Assert.Equal("en", normalized.Language);
        Assert.Equal("#FF3399", normalized.Accent);
    }

    [Theory]
    [InlineData("12", "1:05 PM")]
    [InlineData("24", "13:05")]
    public void FormatClockTime_UsesSelectedFormat(string format, string expected)
    {
        var time = new DateTime(2026, 1, 1, 13, 5, 0);

        Assert.Equal(expected, TrayXSettings.FormatClockTime(
            time,
            format,
            System.Globalization.CultureInfo.GetCultureInfo("en-US")));
    }

    [Fact]
    public void Normalize_UsesSystemClockForUnsupportedFormat()
    {
        var settings = TrayXSettings.Default with { ClockFormat = "invalid" };

        Assert.Equal("system", settings.Normalize().ClockFormat);
    }

    [Theory]
    [InlineData("08:30", "08:30")]
    [InlineData("23:59", "23:59")]
    [InlineData("8:30", "09:00")]
    [InlineData("25:00", "09:00")]
    [InlineData("invalid", "09:00")]
    [InlineData(null, "09:00")]
    public void Normalize_ValidatesScheduledProfileTime(string? value, string expected)
    {
        var settings = TrayXSettings.Default with { ScheduledProfileTime = value ?? string.Empty };

        Assert.Equal(expected, settings.Normalize().ScheduledProfileTime);
    }

    [Fact]
    public void ScheduledProfileSettings_SurviveConfigurationSerialization()
    {
        var profileId = Guid.NewGuid();
        var settings = TrayXSettings.Default with
        {
            ScheduledProfileId = profileId,
            ScheduledProfileTime = "18:45"
        };

        var restored = TrayXSettingsJson.Deserialize(TrayXSettingsJson.Serialize(settings));

        Assert.Equal(profileId, restored.ScheduledProfileId);
        Assert.Equal("18:45", restored.ScheduledProfileTime);
    }

    [Fact]
    public void ProfileDto_RoundTripsPartialAudioApplicationResultThroughIpc()
    {
        var profile = new ProfileDto(
            Guid.NewGuid(),
            "Work",
            45,
            false,
            "dark",
            "#FF3399",
            DateTimeOffset.UtcNow,
            AudioApplied: false);

        var payload = IpcJson.ToElement(profile);
        var result = IpcJson.FromElement<ProfileDto>(payload);

        Assert.NotNull(result);
        Assert.False(result.AudioApplied);
    }

    [Fact]
    public void ProfileDto_ReadsLegacyIpcPayloadWithoutAudioResult()
    {
        var profile = System.Text.Json.JsonSerializer.Deserialize<ProfileDto>(
            """{"id":"00000000-0000-0000-0000-000000000001","name":"Work","volumePercent":45,"muted":false,"windowsTheme":"dark","accent":"#FF3399","createdAt":"2026-01-01T00:00:00+00:00"}""",
            IpcJson.Options);

        Assert.NotNull(profile);
        Assert.Null(profile.AudioApplied);
    }

    [Fact]
    public void Normalize_RemovesDuplicateAndEmptyDisabledDisplays()
    {
        var settings = TrayXSettings.Default with
        {
            DisabledTrayPanelDisplays = ["DISPLAY2", "display2", " ", "DISPLAY3"]
        };

        Assert.Equal(
            ["DISPLAY2", "DISPLAY3"],
            settings.Normalize().DisabledTrayPanelDisplays);
    }

    [Fact]
    public void DisabledTrayPanelDisplayIds_SurviveConfigurationSerialization()
    {
        var settings = TrayXSettings.Default with
        {
            DisabledTrayPanelDisplays = ["MONITOR\\ACME123\\INSTANCE"]
        };

        var json = JsonSerializer.Serialize(settings);
        var restored = JsonSerializer.Deserialize<TrayXSettings>(json);

        Assert.NotNull(restored);
        Assert.Equal(
            settings.DisabledTrayPanelDisplays,
            restored.Normalize().DisabledTrayPanelDisplays);
    }

    [Fact]
    public void Normalize_RemovesDuplicateAndEmptyFavoriteAudioDevices()
    {
        var settings = TrayXSettings.Default with
        {
            FavoriteAudioDeviceIds = ["endpoint-a", "ENDPOINT-A", " ", "endpoint-b"]
        };

        Assert.Equal(
            ["endpoint-a", "endpoint-b"],
            settings.Normalize().FavoriteAudioDeviceIds);
    }

    [Fact]
    public void Normalize_LimitsPersistedSettingsCollections()
    {
        var settings = TrayXSettings.Default with
        {
            DisabledTrayPanelDisplays = Enumerable.Range(0, 1001)
                .Select(index => $"DISPLAY{index}")
                .ToArray(),
            FavoriteAudioDeviceIds = Enumerable.Range(0, 1001)
                .Select(index => $"endpoint-{index}")
                .ToArray(),
            TrayAppRules = Enumerable.Range(0, 1001)
                .Select(index => new TrayAppRuleSettings
                {
                    Id = Guid.NewGuid(),
                    Name = $"Rule {index}",
                    ProcessName = "app.exe"
                })
                .ToArray()
        };

        var normalized = settings.Normalize();

        Assert.Equal(1000, normalized.DisabledTrayPanelDisplays.Count);
        Assert.Equal(1000, normalized.FavoriteAudioDeviceIds.Count);
        Assert.Equal(1000, normalized.TrayAppRules.Count);
    }

    [Fact]
    public void Normalize_DropsOversizedIdentifiersAndDisablesOversizedRules()
    {
        var tooLongProcessName = new string('p', 261);
        var settings = TrayXSettings.Default with
        {
            DeviceSoundPath = new string('x', 32768),
            DisabledTrayPanelDisplays = [new string('d', 513), "DISPLAY1"],
            FavoriteAudioDeviceIds = [new string('a', 2049), "endpoint-1"],
            TrayAppRules =
            [
                new TrayAppRuleSettings
                {
                    Name = new string('n', 129),
                    ProcessName = tooLongProcessName,
                    AutoHide = true
                }
            ]
        };

        var normalized = settings.Normalize();
        var rule = Assert.Single(normalized.TrayAppRules);

        Assert.Empty(normalized.DeviceSoundPath);
        Assert.Equal(["DISPLAY1"], normalized.DisabledTrayPanelDisplays);
        Assert.Equal(["endpoint-1"], normalized.FavoriteAudioDeviceIds);
        Assert.Equal(128, rule.Name.Length);
        Assert.Equal(260, rule.ProcessName.Length);
        Assert.False(rule.IsEnabled);
        Assert.True(rule.AutoHide);
    }

    [Fact]
    public void FavoriteAudioDevices_SurviveConfigurationSerialization()
    {
        var settings = TrayXSettings.Default with
        {
            FavoriteAudioDeviceIds = ["endpoint-a", "endpoint-b"]
        };

        var json = JsonSerializer.Serialize(settings);
        var restored = JsonSerializer.Deserialize<TrayXSettings>(json);

        Assert.NotNull(restored);
        Assert.Equal(TrayXSettings.CurrentConfigVersion, restored.ConfigVersion);
        Assert.Equal(settings.FavoriteAudioDeviceIds, restored.FavoriteAudioDeviceIds);
    }

    [Fact]
    public void SettingsJson_RoundTripsRuntimePreferences()
    {
        var settings = TrayXSettings.Default with
        {
            Language = "en",
            FavoriteAudioDeviceIds = ["endpoint-a"],
            DisabledTrayPanelDisplays = ["MONITOR\\ACME123\\INSTANCE"],
            TrayLivePollMs = 7000,
            DeviceSoundPath = "  C:\\Sounds\\device.wav  "
        };

        var restored = TrayXSettingsJson.Deserialize(TrayXSettingsJson.Serialize(settings));

        Assert.Equal("en", restored.Language);
        Assert.Equal(settings.FavoriteAudioDeviceIds, restored.FavoriteAudioDeviceIds);
        Assert.Equal(settings.DisabledTrayPanelDisplays, restored.DisabledTrayPanelDisplays);
        Assert.Equal(7000, restored.TrayLivePollMs);
        Assert.Equal("C:\\Sounds\\device.wav", restored.DeviceSoundPath);
    }

    [Theory]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("{")]
    public void SettingsJson_RejectsInvalidConfiguration(string json)
    {
        Assert.ThrowsAny<Exception>(() => TrayXSettingsJson.Deserialize(json));
    }

    [Fact]
    public void SettingsJson_RejectsNewerConfigurationVersion()
    {
        var json = $$"""{"configVersion":{{TrayXSettings.CurrentConfigVersion + 1}}}""";

        Assert.Throws<System.Text.Json.JsonException>(() => TrayXSettingsJson.Deserialize(json));
        Assert.Throws<System.Text.Json.JsonException>(() =>
            TrayXSettingsJson.EnsureSupportedVersion(
                TrayXSettings.Default with { ConfigVersion = TrayXSettings.CurrentConfigVersion + 1 }));
    }
}
