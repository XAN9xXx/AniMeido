using Uno.UI.Hosting;
using AniMeido.App.Services;

namespace AniMeido.App;

internal static class Program
{
    [STAThread]
    public static void Main()
    {
        var host = UnoPlatformHostBuilder.Create()
            .FontProvider(DesktopFontProvider.Shared)
            .App(() => new App())
            .UseWin32()
            .Build();
        host.Run();
    }
}
