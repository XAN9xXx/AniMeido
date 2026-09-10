using AniMeido.App.Services;
using System.Reflection;
using System.Runtime.InteropServices;

namespace AniMeido.Tests;

public sealed class TrayIconInteropTests
{
    [Fact]
    public void ShellNotifyIcon_UsesUnicodeWin32EntryPoint()
    {
        var method = typeof(TrayIconService).GetMethod(
            "ShellNotifyIcon",
            BindingFlags.NonPublic | BindingFlags.Static);

        var import = Assert.IsType<DllImportAttribute>(
            method?.GetCustomAttribute<DllImportAttribute>());
        Assert.Equal("shell32.dll", import.Value);
        Assert.Equal("Shell_NotifyIconW", import.EntryPoint);
        Assert.True(import.ExactSpelling);
    }
}
