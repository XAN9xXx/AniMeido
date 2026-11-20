using Microsoft.Win32;

namespace AniMeido.App.Services;

internal static class InstallationIdentity
{
    // Installer/AniMeido.iss 的 AppId 必须与此常量一致（不是 AppUserModelID）。
    internal const string AppId = "{0978F867-ECE7-4110-9BEB-E976B7B8AC25}";
    internal const string LegacyAppId = "{90DF1984-8380-4E35-B350-A269548C06E8}";
    internal const string UninstallRoot = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\";
    internal const string UninstallKey = UninstallRoot + AppId + "_is1";
    internal const string LegacyUninstallKey = UninstallRoot + LegacyAppId + "_is1";
}

internal sealed record UninstallEntry(string InstallLocation, string UninstallString);

/// <summary>测试替换整个注册表边界，不读取或修改真实卸载登记。</summary>
internal interface IInstallationRegistry
{
    UninstallEntry? ReadLegacyInstallation(RegistryHive hive);
    void UpdateDisplayVersionIfPresent(string version);
}

internal sealed class InstallationRegistry : IInstallationRegistry
{
    public UninstallEntry? ReadLegacyInstallation(RegistryHive hive)
    {
        using var root = RegistryKey.OpenBaseKey(hive, RegistryView.Registry64);
        using var key = root.OpenSubKey(InstallationIdentity.LegacyUninstallKey);
        if (key is null)
            return null;
        return new UninstallEntry(
            key.GetValue("InstallLocation") as string ?? "",
            key.GetValue("UninstallString") as string ?? "");
    }

    public void UpdateDisplayVersionIfPresent(string version)
    {
        using var root = RegistryKey.OpenBaseKey(RegistryHive.CurrentUser, RegistryView.Registry64);
        // 开发安装不创建登记；只能打开已有键，不能 CreateSubKey。
        using var key = root.OpenSubKey(InstallationIdentity.UninstallKey, writable: true);
        key?.SetValue("DisplayVersion", version, RegistryValueKind.String);
    }
}
