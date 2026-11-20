extern alias PlayerWebView2;

using PlayerWebView2::Microsoft.Web.WebView2.Core;
using System.Drawing;
using Windows.Foundation;

namespace AniMeido.Plugin.Player.Sources.Web;

internal static class PlayerDesktopWebView
{
    private static readonly Lazy<bool> NativeLoader = new(ConfigureNativeLoader);

    public static Task<CoreWebView2Environment> CreateEnvironmentAsync(string dataDirectory)
    {
        _ = NativeLoader.Value;
        return CoreWebView2Environment.CreateAsync(null, dataDirectory, new CoreWebView2EnvironmentOptions());
    }

    private static bool ConfigureNativeLoader()
    {
        var directory = Path.GetDirectoryName(typeof(PlayerDesktopWebView).Assembly.Location)
            ?? throw new InvalidOperationException("找不到播放器插件目录。");
        var core = typeof(CoreWebView2Environment).Assembly;
        if (!string.Equals(core.Location, Path.Combine(directory, "Microsoft.Web.WebView2.Core.dll"),
                StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("播放器需要使用插件包内的 WebView2 Core。");
        if (!File.Exists(Path.Combine(directory, "WebView2Loader.dll")))
            throw new InvalidOperationException("播放器安装包缺少 win-x64 WebView2Loader.dll。");

        // 只设置当前私有 Core 实例，不改进程 DLL 搜索路径，也不影响宿主的 WebView2Aot。
        CoreWebView2Environment.SetLoaderDllFolderPath(directory);
        return true;
    }

    public static Rectangle ToPixelBounds(Rect logical, double scale)
    {
        var x = (int)Math.Round(logical.X * scale);
        var y = (int)Math.Round(logical.Y * scale);
        return new Rectangle(x, y,
            Math.Max(1, (int)Math.Round(logical.Right * scale) - x),
            Math.Max(1, (int)Math.Round(logical.Bottom * scale) - y));
    }
}
