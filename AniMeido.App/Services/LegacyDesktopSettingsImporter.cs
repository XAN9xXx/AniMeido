using Microsoft.Win32;

namespace AniMeido.App.Services;

/// <summary>只继承新文件中尚未存在的选择；旧存储仅在首次导入时读取。</summary>
public sealed class LegacyDesktopSettingsImporter
{
    private readonly DesktopSettingsStore _store;
    private readonly Func<Task<LegacyDesktopSettings>> _readLegacy;

    public LegacyDesktopSettingsImporter(DesktopSettingsStore store)
        : this(store, () => Task.FromResult(ReadRegistry()))
    {
    }

    internal LegacyDesktopSettingsImporter(
        DesktopSettingsStore store,
        Func<Task<LegacyDesktopSettings>> readLegacy)
    {
        _store = store;
        _readLegacy = readLegacy;
    }

    public async Task<DesktopSettings> ImportAsync(CancellationToken cancellationToken = default)
    {
        var current = await _store.LoadAsync(cancellationToken);
        if (current.LegacyUiSettingsImported)
        {
            return current;
        }

        var legacy = await _readLegacy();
        cancellationToken.ThrowIfCancellationRequested();
        return await _store.UpdateAsync(latest => latest.LegacyUiSettingsImported
            ? latest
            : latest with
            {
                ThemeSetting = latest.ThemeSetting ?? NormalizeTheme(legacy.ThemeSetting),
                PrivacyAccepted = latest.PrivacyAccepted ?? legacy.PrivacyAccepted,
                AnnouncementShown = latest.AnnouncementShown ?? legacy.AnnouncementShown,
                LegacyUiSettingsImported = true,
            }, cancellationToken);
    }

    internal static string NormalizeTheme(object? value)
        => value is "Light" or "Dark" or "Default" ? (string)value : "Default";

    private static LegacyDesktopSettings ReadRegistry()
    {
        // 隔离开发版不读取日常应用的旧设置，也不继承日常隐私同意。
        if (AppDataRoots.IsIsolated || !OperatingSystem.IsWindows())
        {
            return new LegacyDesktopSettings(null, false, false);
        }

        using var key = Registry.CurrentUser.OpenSubKey(@"Software\AniMeido");
        return new LegacyDesktopSettings(
            key?.GetValue("ThemeSetting"),
            key?.GetValue("PrivacyAccepted") is 1,
            key?.GetValue("AnnouncementShown") is 1);
    }
}

internal sealed record LegacyDesktopSettings(
    object? ThemeSetting,
    bool PrivacyAccepted,
    bool AnnouncementShown);
