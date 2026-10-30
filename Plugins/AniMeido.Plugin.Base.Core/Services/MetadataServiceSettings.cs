namespace AniMeido.Plugin.Base.Services;

internal static class MetadataServiceSettings
{
    internal const string QueryBaseUrl = "https://api.animeido.com:38443";
    internal const string ImageBaseUrl = "https://img.animeido.com:38443";
    internal const string ClientName = "AniMeidoMetadata";

    internal static void ConfigureClient(HttpClient client)
    {
        client.BaseAddress = new Uri(QueryBaseUrl);
        client.Timeout = TimeSpan.FromSeconds(10);
        var version = typeof(MetadataDataSource).Assembly.GetName().Version?.ToString(3);
        client.DefaultRequestHeaders.TryAddWithoutValidation(
            "User-Agent", $"XAN9xXx/AniMeido/{version} (https://github.com/XAN9xXx/AniMeido)");
    }
}
