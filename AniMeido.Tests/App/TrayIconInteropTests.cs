using AniMeido.App.Services;
using System.Reflection;
using System.Runtime.InteropServices;

namespace AniMeido.Tests;

public sealed class TrayIconInteropTests
{
    [Theory]
    [InlineData("RegisterClassEx")]
    [InlineData("CreateWindowEx")]
    [InlineData("LoadIcon")]
    [InlineData("GetMessage")]
    [InlineData("ShellNotifyIcon")]
    [InlineData("RegisterWindowMessage")]
    [InlineData("ChangeWindowMessageFilterEx")]
    [Trait("Category", "Mechanism")]
    // 托盘失败路径使用 GetLastWin32Error；运行库必须先保存对应调用的错误码。
    public void ReportedNativeFailures_PreserveTheirOwnWin32Error(string methodName)
    {
        var method = typeof(TrayIconService).GetMethod(
            methodName,
            BindingFlags.NonPublic | BindingFlags.Static);

        var import = Assert.IsType<DllImportAttribute>(
            method?.GetCustomAttribute<DllImportAttribute>());

        Assert.True(import.SetLastError);
    }

    [Fact]
    [Trait("Category", "Mechanism")]
    // 检查的就是私有 P/Invoke 声明和 Unicode 入口，不实际创建托盘图标。
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

    [Fact]
    [Trait("Category", "Mechanism")]
    public void TaskbarMessageRegistration_UsesTheUnicodeEntryPoint()
    {
        var method = typeof(TrayIconService).GetMethod(
            "RegisterWindowMessage", BindingFlags.NonPublic | BindingFlags.Static);
        var import = Assert.IsType<DllImportAttribute>(method?.GetCustomAttribute<DllImportAttribute>());
        Assert.Equal("RegisterWindowMessageW", import.EntryPoint);
        Assert.True(import.ExactSpelling);
        Assert.Equal(CharSet.Unicode, import.CharSet);
    }
}
