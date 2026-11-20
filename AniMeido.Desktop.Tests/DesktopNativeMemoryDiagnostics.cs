using AniMeido.App.Services;
using AniMeido.App.Themes;
using AniMeido.Contracts;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using SkiaSharp;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using Uno.UI.Hosting;
using Windows.Graphics;
using Xunit;
using Xunit.Abstractions;

namespace AniMeido.Desktop.Tests;

#pragma warning disable CA1031 // 独立 UI 线程和 async void 的异常交回 xUnit 完成任务。

// 诊断只在新测试进程中显式启用，普通测试与 CI 继续使用原 Headless 宿主。
public sealed class ExplicitNativeMemoryDiagnosticAttribute : FactAttribute
{
    public ExplicitNativeMemoryDiagnosticAttribute()
    {
        if (Environment.GetEnvironmentVariable("ANIMEIDO_NATIVE_MEMORY_DIAGNOSTIC") != "1")
            Skip = "仅在独立进程中显式启用原生内存诊断。";
    }
}

public sealed class DesktopNativeMemoryDiagnostics(ITestOutputHelper output)
{
    private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    [ExplicitNativeMemoryDiagnostic]
    [Trait("Category", "PerformanceDiagnostic")]
    [Trait("Category", "Mechanism")]
    public async Task ColdWin32FrameAndCommonChineseTextMeasureHostAndFontCosts()
    {
        Assert.Null(Application.Current);
        var directory = Directory.CreateTempSubdirectory("AniMeido-native-memory-");
        Environment.SetEnvironmentVariable("ANIMEIDO_DATA_ROOT", directory.FullName);
        var store = new DesktopSettingsStore(new DiagnosticPaths(directory.FullName));
        await store.SaveAsync(new DesktopSettings
        {
            VisualStyle = "Paper", FontStyle = "Serif",
            ThemeSetting = Environment.GetEnvironmentVariable("ANIMEIDO_NATIVE_MEMORY_THEME") ?? "Light",
        });
        var completed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                UnoPlatformHostBuilder.Create().FontProvider(DesktopFontProvider.Shared)
                    .App(() => new DiagnosticApplication(store, output, completed))
                    .UseWin32().Build().Run();
                stopped.TrySetResult();
            }
            catch (Exception error) { completed.TrySetException(error); stopped.TrySetException(error); }
        }) { IsBackground = true, Name = "Explicit Uno Win32 memory diagnostic" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        await completed.Task.WaitAsync(TimeSpan.FromSeconds(120));
        await stopped.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(thread.Join(TimeSpan.FromSeconds(5)), "诊断原生消息循环应正常退出。");
        directory.Delete(recursive: true);
        output.WriteLine($"native-host-stopped=True; pid={Environment.ProcessId}");
    }

    private sealed class DiagnosticApplication(DesktopSettingsStore store, ITestOutputHelper output,
        TaskCompletionSource completed) : Application
    {
        protected override async void OnLaunched(LaunchActivatedEventArgs args)
        {
            Window? window = null;
            ThemeService? theme = null;
            Exception? failure = null;
            var frames = 0;
            var firstFrame = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            void OnRendering(object? sender, object value)
            {
                CompositionTarget.Rendering -= OnRendering;
                frames++;
                firstFrame.TrySetResult();
            }
            CompositionTarget.Rendering += OnRendering;
            try
            {
                Resources = new ResourceDictionary();
                Resources.MergedDictionaries.Add(new XamlControlsResources());
                Resources.MergedDictionaries.Add(new DesktopInteractionControls());
                Resources.MergedDictionaries.Add(new DesktopThemeResources());
                Resources.MergedDictionaries.Add(new DesktopWindowsSectionNavigation());
                Resources.MergedDictionaries.Add(new DesktopSectionChoices());
                var frame = new Frame();
                var root = new Grid();
                root.Children.Add(frame);
                window = new Window { Title = "AniMeido 原生空外壳内存诊断", Content = root };
                var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                window.Closed += (_, _) => closed.TrySetResult();
                window.Activate();
                var handle = DesktopNativeWindow.GetHandle(window);
                for (var adjustment = 0; adjustment < 3; adjustment++)
                {
                    Assert.True(Native.GetClientRect(handle, out var rect));
                    if (rect.Right == 1345 && rect.Bottom == 802) break;
                    window.AppWindow.Resize(new SizeInt32(window.AppWindow.Size.Width + 1345 - rect.Right,
                        window.AppWindow.Size.Height + 802 - rect.Bottom));
                    await Task.Delay(50);
                }
                Assert.True(Native.GetClientRect(handle, out var actual));
                Assert.Equal(1345, actual.Right);
                Assert.Equal(802, actual.Bottom);
                Assert.Equal(120u, Native.GetDpiForWindow(handle));
                theme = new ThemeService(store);
                await theme.InitializeThemeAsync(root);
                root.Background = (Brush)Resources["DesktopPageBrush"];
                await firstFrame.Task.WaitAsync(TimeSpan.FromSeconds(10));
                root.UpdateLayout();
                var timer = Stopwatch.StartNew();
                var startGen2 = GC.CollectionCount(2);
                WriteSample("first-native-frame", root, handle, timer, frames, startGen2);
                Assert.False(FontsLoaded());
                await StableStageAsync("empty-frame", root, handle, timer, () => frames, startGen2);

                output.WriteLine($"font-trigger-ms={timer.ElapsedMilliseconds}; beforeLoaded={FontsLoaded()}");
                var text = new StackPanel { Margin = new Thickness(32) };
                foreach (var size in new[] { 17d, 18d, 24d })
                    text.Children.Add(new TextBlock
                    {
                        Text = "番剧库 今天 推荐 作品详情 March 8.36",
                        FontSize = size,
                        FontFamily = new FontFamily(DesktopAppearanceFont.ForStyle(DesktopFontStyle.Serif).Family),
                        Foreground = (Brush)Resources["DesktopTextBrush"],
                    });
                frame.Content = text;
                root.UpdateLayout();
                var deadline = Stopwatch.StartNew();
                while (!FontsLoaded() && deadline.Elapsed < TimeSpan.FromSeconds(5)) await Task.Delay(16);
                Assert.True(FontsLoaded());
                output.WriteLine($"font-loaded-ms={timer.ElapsedMilliseconds}");
                firstFrame = new(TaskCreationOptions.RunContinuationsAsynchronously);
                CompositionTarget.Rendering += OnRendering;
                text.InvalidateMeasure();
                root.UpdateLayout();
                await firstFrame.Task.WaitAsync(TimeSpan.FromSeconds(10));
                WriteSample("first-chinese-layout", root, handle, timer, frames, startGen2);
                await StableStageAsync("chinese-text", root, handle, timer, () => frames, startGen2);
                frame.Content = null;
                await theme.DisposeAsync();
                theme = null;
                window.Close();
                await closed.Task.WaitAsync(TimeSpan.FromSeconds(5));
                window = null;
                output.WriteLine($"native-window-closed=True; frames={frames}");
            }
            catch (Exception error) { failure = error; }
            finally
            {
                CompositionTarget.Rendering -= OnRendering;
                if (theme is not null) await theme.DisposeAsync();
                window?.Close();
                Exit();
                if (failure is null) completed.TrySetResult(); else completed.TrySetException(failure);
            }
        }

        private async Task StableStageAsync(string stage, Grid root, nint handle, Stopwatch timer, Func<int> frames, int gen2)
        {
            var samples = new List<(double Private, double Working, double Committed)>();
            for (var sample = 0; sample < 15; sample++)
            {
                await Task.Delay(2000);
                samples.Add(WriteSample($"{stage}-{sample + 1}", root, handle, timer, frames(), gen2));
            }
            output.WriteLine($"stable-stage={stage}; seconds=30; privateMiB={samples.Average(item => item.Private):F2}/{samples.Min(item => item.Private):F2}/{samples.Max(item => item.Private):F2}; workingMiB={samples.Average(item => item.Working):F2}/{samples.Min(item => item.Working):F2}/{samples.Max(item => item.Working):F2}; committedMiB={samples.Average(item => item.Committed):F2}/{samples.Min(item => item.Committed):F2}/{samples.Max(item => item.Committed):F2}");
        }

        private (double Private, double Working, double Committed) WriteSample(string stage, Grid root, nint handle,
            Stopwatch timer, int frames, int gen2)
        {
            using var process = Process.GetCurrentProcess();
            Assert.True(Native.GetClientRect(handle, out var rect));
            var xamlRoot = Assert.IsType<XamlRoot>(root.XamlRoot);
            var memory = (Private: process.PrivateMemorySize64 / 1048576d, Working: process.WorkingSet64 / 1048576d,
                Committed: GC.GetGCMemoryInfo().TotalCommittedBytes / 1048576d);
            var cache = (System.Collections.IDictionary)typeof(DesktopFontProvider).GetField("_chinese", Flags)!.GetValue(DesktopFontProvider.Shared)!;
            var rootImages = (typeof(XamlRoot).GetField("Changed", Flags)!.GetValue(xamlRoot) as Delegate)?.GetInvocationList()
                .Count(handler => handler.Target?.GetType().DeclaringType?.Name == "ManagedImageLoader") ?? 0;
            Assert.Equal(0, rootImages);
            Assert.False(ContainsImage(root));
            output.WriteLine($"native-stage={stage}; pid={Environment.ProcessId}; elapsedMs={timer.ElapsedMilliseconds}; frames={frames}; physicalClient={rect.Right}x{rect.Bottom}; dpi={Native.GetDpiForWindow(handle)}; logicalRoot={root.ActualWidth}x{root.ActualHeight}; scale={xamlRoot.RasterizationScale}; theme={root.ActualTheme}; style=Paper; font=Serif; filesLoaded={FontsLoaded()}; cachedSizes={cache.Count}; privateMiB={memory.Private:F2}; workingMiB={memory.Working:F2}; managedMiB={GC.GetTotalMemory(false) / 1048576d:F2}; committedMiB={memory.Committed:F2}; gen0={GC.CollectionCount(0)}; gen1={GC.CollectionCount(1)}; gen2={GC.CollectionCount(2) - gen2}; skiaFontBytes={SKGraphics.GetFontCacheUsed()}; skiaResourceBytes={SKGraphics.GetResourceCacheTotalBytesUsed()}; imageRootSubscriptions={rootImages}; images=0; {Native.Partitions()}");
            return memory;
        }

        private static bool ContainsImage(DependencyObject parent)
        {
            if (parent is Image) return true;
            for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
                if (ContainsImage(VisualTreeHelper.GetChild(parent, i))) return true;
            return false;
        }

        private static bool FontsLoaded()
        {
            var files = typeof(DesktopFontProvider).GetField("_files", Flags)!.GetValue(DesktopFontProvider.Shared)!;
            return (bool)files.GetType().GetProperty("IsValueCreated")!.GetValue(files)!
                && ((Task)files.GetType().GetProperty("Value")!.GetValue(files)!).IsCompletedSuccessfully;
        }
    }

    private sealed class DiagnosticPaths(string directory) : IAppDataPaths
    {
        public string DatabasePath => Path.Combine(directory, "unused.db");
        public string BackupDirectory => Path.Combine(directory, "backups");
        public string LogDirectory => Path.Combine(directory, "logs");
    }

    private static class Native
    {
        [StructLayout(LayoutKind.Sequential)]
        internal struct Rect { internal int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)]
        private struct Region
        {
            internal nuint Address, AllocationBase;
            internal uint AllocationProtect;
            internal ushort PartitionId;
            internal nuint Size;
            internal uint State, Protect, Type;
        }
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetClientRect(nint window, out Rect rect);
        [DllImport("user32.dll")]
        internal static extern uint GetDpiForWindow(nint window);
        [DllImport("kernel32.dll", EntryPoint = "VirtualQuery")]
        private static extern nuint Query(nuint address, out Region region, nuint length);

        internal static string Partitions()
        {
            ulong privateBytes = 0, imageBytes = 0, mappedBytes = 0, writeCombineBytes = 0;
            var allocations = new Dictionary<nuint, ulong>();
            nuint address = 0;
            while (Query(address, out var region, (nuint)Marshal.SizeOf<Region>()) != 0)
            {
                if (region.State == 0x1000)
                {
                    if (region.Type == 0x20000)
                    {
                        privateBytes += region.Size;
                        allocations[region.AllocationBase] = allocations.GetValueOrDefault(region.AllocationBase) + region.Size;
                        if ((region.Protect & 0x400) != 0) writeCombineBytes += region.Size;
                    }
                    else if (region.Type == 0x1000000) imageBytes += region.Size;
                    else if (region.Type == 0x40000) mappedBytes += region.Size;
                }
                var next = region.Address + region.Size;
                if (next <= address) break;
                address = next;
            }
            return $"virtualCommittedPrivateMiB={privateBytes / 1048576d:F2}; virtualCommittedImageMiB={imageBytes / 1048576d:F2}; virtualCommittedMappedMiB={mappedBytes / 1048576d:F2}; privateWriteCombineMiB={writeCombineBytes / 1048576d:F2}; largestPrivateAllocationsMiB=[{string.Join(",", allocations.Values.OrderDescending().Take(6).Select(value => (value / 1048576d).ToString("F2", System.Globalization.CultureInfo.InvariantCulture)))}]";
        }
    }
}
