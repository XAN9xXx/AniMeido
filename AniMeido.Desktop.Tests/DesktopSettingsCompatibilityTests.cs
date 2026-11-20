using System.Text.Json;
using AniMeido.App.Services;
using Xunit;

namespace AniMeido.Desktop.Tests;

public sealed class DesktopSettingsCompatibilityTests
{
    [Fact]
    public void ExistingSettingsKeepTheirValuesAndReceiveDesktopDefaults()
    {
        var saved = JsonSerializer.Deserialize<DesktopSettings>(
            """{"KeepInTrayOnClose":true,"ThemeSetting":"Dark","PrivacyAccepted":true,"UpdateChannel":"preview","SkippedUpdateVersion":"3.0.0"}""")!;

        Assert.True(saved.KeepInTrayOnClose);
        Assert.True(saved.PrivacyAccepted);
        Assert.Equal("Dark", saved.ThemeSetting);
        Assert.Equal("preview", saved.UpdateChannel);
        Assert.Equal("3.0.0", saved.SkippedUpdateVersion);
        Assert.Equal("Fluent11", saved.VisualStyle);
        Assert.Equal("Cover", saved.CatalogCardStyle);
        Assert.False(saved.ShowPlanReminders);
    }

    [Fact]
    public void DesktopChoicesRoundTripTogetherWithTheExistingSettings()
    {
        var saved = new DesktopSettings
        {
            VisualStyle = "Paper",
            FontStyle = "Serif",
            CatalogCardStyle = "Detailed",
            ShowPlanReminders = true,
            KeepInTrayOnClose = true,
            PrivacyAccepted = true,
            LegacyUiSettingsImported = true,
            UpdateChannel = "preview",
        };

        var restored = JsonSerializer.Deserialize<DesktopSettings>(JsonSerializer.Serialize(saved));

        Assert.Equal(saved, restored);
    }
}
