using Microsoft.Extensions.DependencyInjection;

namespace AniMeido.App.Services;

internal static class DatabaseStartup
{
    public static async Task InitializeAsync(IServiceProvider provider)
    {
        var db = provider.GetRequiredService<DatabaseService>();
        await db.InitializeAsync();
    }
}
