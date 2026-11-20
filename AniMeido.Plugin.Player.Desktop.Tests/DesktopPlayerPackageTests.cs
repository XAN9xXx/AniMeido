using System.IO.Compression;
using System.Drawing;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Text.Json;
using AniMeido.Contracts;
using Microsoft.UI.Xaml;
using Windows.Foundation;
using Xunit;

namespace AniMeido.Plugin.Player.Desktop.Tests;

[Collection("Desktop Player 包")]
public sealed class DesktopPlayerPackageTests
{
    private const string Entry = "AniMeido.Plugin.Player.dll";
    private const string WebView = "Microsoft.Web.WebView2.Core.dll";
    private static readonly string[] Payload =
    [
        "Acornima.dll", "AngleSharp.dll", Entry, "HtmlAgilityPack.dll", "Jint.dll",
        WebView, "WebView2Loader.dll", "libmpv-2.dll",
    ];

    [PackageFact]
    public void LatestPackageHasOnlyItsOwnFilesAndTheNewCompatibilityLine()
    {
        using var zip = OpenPackage();
        Assert.Equal(Payload.Append("plugin.json").Order(StringComparer.Ordinal),
            zip.Entries.Select(entry => entry.FullName).Order(StringComparer.Ordinal));
        using var template = JsonDocument.Parse(zip.GetEntry("plugin.json")!.Open());
        var manifest = template.RootElement;
        Assert.Equal(2, manifest.GetProperty("formatVersion").GetInt32());
        Assert.Equal("AniMeido.Plugin.Player", manifest.GetProperty("pluginId").GetString());
        Assert.Equal("0.5.1", manifest.GetProperty("version").GetString());
        Assert.Equal("2.1.3", manifest.GetProperty("minAppVersion").GetString());
        Assert.Equal(Entry, manifest.GetProperty("entryAssembly").GetString());
        Assert.Equal(Payload.Order(StringComparer.Ordinal), manifest.GetProperty("files").EnumerateArray()
            .Select(file => file.GetProperty("path").GetString()).Order(StringComparer.Ordinal));
        Assert.Contains(manifest.GetProperty("activationEvents").EnumerateArray(), item => item.GetString() == "onAnimePlayback");
        Assert.Contains(manifest.GetProperty("contributes").GetProperty("capabilities").EnumerateArray(),
            item => item.GetString() == "animePlayback");

        using var player = ReadPe(zip.GetEntry(Entry)!);
        var reader = player.GetMetadataReader();
        var references = reader.AssemblyReferences.Select(handle => reader.GetString(reader.GetAssemblyReference(handle).Name)).ToArray();
        Assert.Contains("Uno.UI", references);
        Assert.Contains("Microsoft.Web.WebView2.Core", references);
        Assert.DoesNotContain("Microsoft.Web.WebView2.Core.Projection", references);
        Assert.DoesNotContain("Microsoft.WinUI", references);
        Assert.Equal(new Version(0, 5, 1, 0), reader.GetAssemblyDefinition().Version);
        using var webView = ReadPe(zip.GetEntry(WebView)!);
        Assert.True(webView.HasMetadata);

        foreach (var name in new[] { "libmpv-2.dll", "WebView2Loader.dll" })
        {
            using var image = ReadPe(zip.GetEntry(name)!);
            Assert.Equal(Machine.Amd64, image.PEHeaders.CoffHeader.Machine);
            Assert.False(image.HasMetadata);
        }
    }

    [PackageFact]
    public void HostedPluginResolvesUnoFromTheHostAndWebViewFromItsOwnDirectory()
    {
        using var zip = OpenPackage();
        var root = Directory.CreateTempSubdirectory("AniMeido-player-package-");
        var context = new PluginFiles(root.FullName);
        try
        {
            zip.ExtractToDirectory(root.FullName);
            var assembly = context.LoadFromAssemblyName(new("AniMeido.Plugin.Player"));
            Assert.IsAssignableFrom<IPlugin>(Activator.CreateInstance(assembly.GetType("AniMeido.Plugin.Player.PlayerPlugin", throwOnError: true)!));
            foreach (var name in new[] { "PlayerWindow", "SourceManagementWindow", "SourceVerificationWindow" })
            {
                var type = assembly.GetType($"AniMeido.Plugin.Player.Views.{name}", throwOnError: true)!;
                Assert.Same(typeof(Window), type.BaseType);
            }
            var webView = context.LoadFromAssemblyName(new("Microsoft.Web.WebView2.Core"));
            Assert.Same(context, AssemblyLoadContext.GetLoadContext(webView));
            Assert.Equal(Path.Combine(root.FullName, WebView), context.LoadedPaths["Microsoft.Web.WebView2.Core"]);
            Assert.NotNull(webView.GetType("Microsoft.Web.WebView2.Core.CoreWebView2Environment")!
                .GetMethod("CreateAsync", BindingFlags.Static | BindingFlags.Public));

            CheckNative(root.FullName, "libmpv-2.dll", "mpv_client_api_version");
            CheckNative(root.FullName, "WebView2Loader.dll", "GetAvailableCoreWebView2BrowserVersionString");
            Assert.NotNull(assembly.GetType("AniMeido.Plugin.Player.Views.SourceVerificationWindow")!
                .GetMethod("OnNativeWindowClosing", BindingFlags.NonPublic | BindingFlags.Instance));
            var bounds = assembly.GetType("AniMeido.Plugin.Player.Sources.Web.PlayerDesktopWebView")!
                .GetMethod("ToPixelBounds")!.Invoke(null, [new Rect(10.25, 70.6, 100.5, 200.3), 1.25]);
            Assert.Equal(new Rectangle(13, 88, 125, 251), Assert.IsType<Rectangle>(bounds));
            CheckPlayerChildWindow(assembly);
        }
        finally
        {
            context.Unload();
            root.Delete(recursive: true);
        }
    }

    private static ZipArchive OpenPackage()
    {
        var path = Environment.GetEnvironmentVariable(PackageFactAttribute.Variable)!;
        Assert.True(Path.IsPathFullyQualified(path) && File.Exists(path), $"找不到新 Player 安装包：{path}");
        return ZipFile.OpenRead(path);
    }

    private static PEReader ReadPe(ZipArchiveEntry entry)
    {
        using var source = entry.Open();
        var content = new MemoryStream();
        source.CopyTo(content);
        content.Position = 0;
        return new PEReader(content);
    }

    private static void CheckNative(string directory, string name, string entryPoint)
    {
        var library = NativeLibrary.Load(Path.Combine(directory, name));
        try { Assert.True(NativeLibrary.TryGetExport(library, entryPoint, out var address) && address != 0); }
        finally { NativeLibrary.Free(library); }
    }

    private static void CheckPlayerChildWindow(Assembly player)
    {
        // 只创建隔离的隐藏系统窗口。验证实际插件的视频子 HWND，不启动 Uno 界面或播放内容。
        var parent = CreateWindow(0, "STATIC", string.Empty, 0x80000000, 0, 0, 16, 16, 0, 0, 0, 0);
        Assert.NotEqual(nint.Zero, parent);
        try
        {
            var type = player.GetType("AniMeido.Plugin.Player.Playback.NativeVideoHost", throwOnError: true)!;
            using var target = Assert.IsAssignableFrom<IDisposable>(Activator.CreateInstance(type, parent));
            var handle = Assert.IsType<nint>(type.GetProperty("Handle")!.GetValue(target));
            Assert.NotEqual(nint.Zero, handle);
            Assert.Equal(parent, GetParent(handle));
            target.Dispose();
            Assert.False(IsWindow(handle));
        }
        finally { _ = DestroyWindow(parent); }
    }

    [DllImport("user32.dll", EntryPoint = "CreateWindowExW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint CreateWindow(uint extendedStyle, string className, string title, uint style,
        int x, int y, int width, int height, nint parent, nint menu, nint instance, nint parameter);

    [DllImport("user32.dll")]
    private static extern nint GetParent(nint window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(nint window);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyWindow(nint window);

    private sealed class PluginFiles(string directory) : AssemblyLoadContext(isCollectible: true)
    {
        public Dictionary<string, string> LoadedPaths { get; } = new(StringComparer.OrdinalIgnoreCase);

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            if (assemblyName.Name is not { } name) return null;
            var path = Path.Combine(directory, name + ".dll");
            if (!File.Exists(path)) return null;
            LoadedPaths[name] = path;
            // 模拟相同的私有文件优先规则，但结束时不让测试进程继续锁住解包目录。
            using var file = File.OpenRead(path);
            return LoadFromStream(file);
        }
    }
}

internal sealed class PackageFactAttribute : FactAttribute
{
    public const string Variable = "ANIMEIDO_DESKTOP_PLAYER_PACKAGE";

    public PackageFactAttribute()
    {
        if (!OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(Variable)))
            Skip = $"请先生成 win-x64 Player 包并设置 {Variable}。";
    }
}
