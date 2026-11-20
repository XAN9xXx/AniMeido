using Uno.UI.Hosting;

namespace AniMeido.PluginHost;

internal static class Program
{
    [STAThread]
    public static int Main()
    {
        var args = Environment.GetCommandLineArgs();
        if (string.IsNullOrWhiteSpace(CommandLineOptions.GetPipeName(args))
            || string.IsNullOrWhiteSpace(CommandLineOptions.GetCallbackPipeName(args)))
            return 2;

        // 插件关闭最后一个窗口后仍可被主程序再次调用；管道关闭时由 App 显式退出。
        UnoPlatformHostBuilder.Create().App(() => new App
        {
            DispatcherShutdownMode = Microsoft.UI.Xaml.DispatcherShutdownMode.OnExplicitShutdown,
        }).UseWin32().Build().Run();
        return 0;
    }
}
