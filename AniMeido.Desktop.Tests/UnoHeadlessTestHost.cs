using AniMeido.App.Services;
using AniMeido.App.Themes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Uno.UI.Hosting;
using Xunit;

namespace AniMeido.Desktop.Tests;

#pragma warning disable CA1031 // 跨线程转交测试异常给完成任务，由 xUnit 报告原始失败。

[CollectionDefinition(UnoHeadlessTestHost.CollectionName, DisableParallelization = true)]
public sealed class UnoHeadlessTestCollection { }

internal static class UnoHeadlessTestHost
{
    internal const string CollectionName = "Uno Headless";
    private static readonly SemaphoreSlim Queue = new(1, 1);
    private static readonly Lazy<Task<Grid>> Root = new(Start);

    internal static async Task RunAsync(Func<Grid, Task> action)
    {
        await Queue.WaitAsync();
        try
        {
            var root = await Root.Value.WaitAsync(TimeSpan.FromSeconds(30));
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!root.DispatcherQueue.TryEnqueue(async () =>
            {
                try
                {
                    root.Children.Clear();
                    root.Resources.Clear();
                    root.Background = null;
                    root.RequestedTheme = ElementTheme.Default;
                    DesktopFontProvider.Shared.UseSerif = false;
                    Application.Current.Resources = CreateResources();
                    await action(root);
                    completion.TrySetResult();
                }
                catch (Exception error) { completion.TrySetException(error); }
                finally { root.Children.Clear(); }
            }))
                throw new InvalidOperationException("Headless 界面线程已停止。");
            await completion.Task.WaitAsync(TimeSpan.FromSeconds(60));
        }
        finally { Queue.Release(); }
    }

    private static Task<Grid> Start()
    {
        // 卡片的图片缓存也使用这个独立根，整个测试宿主都不接触个人缓存。
        var dataRoot = Path.Combine(Path.GetTempPath(), "AniMeido-headless-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dataRoot);
        Environment.SetEnvironmentVariable("ANIMEIDO_DATA_ROOT", dataRoot);
        var ready = new TaskCompletionSource<Grid>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                UnoPlatformHostBuilder.Create()
                    .FontProvider(DesktopFontProvider.Shared)
                    .App(() => new HeadlessApplication(ready))
                    .UseHeadless(options => options.WithSize(960, 720))
                    .Build().Run();
            }
            catch (Exception error) { ready.TrySetException(error); }
        }) { IsBackground = true, Name = "Uno headless tests" };
        if (OperatingSystem.IsWindows())
            thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return ready.Task;
    }

    private static ResourceDictionary CreateResources()
    {
        var resources = new ResourceDictionary();
        resources.MergedDictionaries.Add(new XamlControlsResources());
        resources.MergedDictionaries.Add(new DesktopInteractionControls());
        resources.MergedDictionaries.Add(new DesktopThemeResources());
        resources.MergedDictionaries.Add(new DesktopWindowsSectionNavigation());
        resources.MergedDictionaries.Add(new DesktopSectionChoices());
        return resources;
    }

    // 只建立离屏窗口与真实资源，不运行应用启动、数据库、通知或插件。
    private sealed class HeadlessApplication(TaskCompletionSource<Grid> ready) : Application
    {
        protected override void OnLaunched(LaunchActivatedEventArgs args)
        {
            try
            {
                Resources = CreateResources();
                var root = new Grid();
                var window = new Window { Content = root };
                window.Activate();
                ready.TrySetResult(root);
            }
            catch (Exception error) { ready.TrySetException(error); }
        }
    }
}
