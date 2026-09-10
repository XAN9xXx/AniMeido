using System.Text.Json.Serialization;

namespace AniMeido.Plugin.Player.Sources.Web;

internal static class WebPageInteractionClassifier
{
    private static readonly string[] VerificationMarkers =
    [
        "verify you are human",
        "checking your browser",
        "just a moment",
        "security verification",
        "human verification",
        "cf-chl-",
        "challenge-platform",
        "attention required! | cloudflare",
        "sorry, you have been blocked",
        "turnstile",
        "recaptcha",
        "hcaptcha",
        "人机验证",
        "安全验证",
        "访问验证",
        "请完成验证",
    ];

    private static readonly string[] LoginPathMarkers =
    [
        "/login",
        "/signin",
        "/sign-in",
        "/account/login",
        "/passport/",
        "/auth/",
    ];

    public static WebPageInteractionKind Classify(WebPageSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (snapshot.HasChallengeElement
            || ContainsAny(snapshot.Title, VerificationMarkers)
            || ContainsAny(snapshot.Text, VerificationMarkers))
        {
            return WebPageInteractionKind.HumanVerification;
        }

        if (snapshot.HasPasswordInput
            || LoginPathMarkers.Any(marker =>
                snapshot.Url.Contains(
                    marker,
                    StringComparison.OrdinalIgnoreCase)))
        {
            return WebPageInteractionKind.Login;
        }

        return WebPageInteractionKind.None;
    }

    private static bool ContainsAny(string value, IEnumerable<string> markers)
        => markers.Any(marker =>
            value.Contains(marker, StringComparison.OrdinalIgnoreCase));
}

internal enum WebPageInteractionKind
{
    None,
    HumanVerification,
    Login,
}

internal sealed class WebPageSnapshot
{
    [JsonPropertyName("url")]
    public string Url { get; set; } = string.Empty;

    [JsonPropertyName("title")]
    public string Title { get; set; } = string.Empty;

    [JsonPropertyName("text")]
    public string Text { get; set; } = string.Empty;

    [JsonPropertyName("hasPasswordInput")]
    public bool HasPasswordInput { get; set; }

    [JsonPropertyName("hasChallengeElement")]
    public bool HasChallengeElement { get; set; }
}
