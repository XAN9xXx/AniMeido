using AniMeido.Plugin.Player.Sources.Web;

namespace AniMeido.Tests;

public sealed class WebPageInteractionClassifierTests
{
    [Fact]
    public void Classify_DetectsCloudflareChallenge()
    {
        var snapshot = new WebPageSnapshot
        {
            Url = "https://example.test/watch/1",
            Title = "Just a moment...",
            Text = "Checking your browser before accessing the site.",
        };

        var result = WebPageInteractionClassifier.Classify(snapshot);

        Assert.Equal(WebPageInteractionKind.HumanVerification, result);
    }

    [Fact]
    public void Classify_DetectsLoginForm()
    {
        var snapshot = new WebPageSnapshot
        {
            Url = "https://example.test/account/login",
            Title = "登录",
            HasPasswordInput = true,
        };

        var result = WebPageInteractionClassifier.Classify(snapshot);

        Assert.Equal(WebPageInteractionKind.Login, result);
    }

    [Fact]
    public void Classify_DoesNotTreatLoginNavigationTextAsLoginPage()
    {
        var snapshot = new WebPageSnapshot
        {
            Url = "https://example.test/watch/1",
            Title = "Episode 1",
            Text = "登录后可以收藏本剧。",
        };

        var result = WebPageInteractionClassifier.Classify(snapshot);

        Assert.Equal(WebPageInteractionKind.None, result);
    }

    [Fact]
    public void Classify_DoesNotTreatCloudflareFooterAsChallenge()
    {
        var snapshot = new WebPageSnapshot
        {
            Url = "https://example.test/watch/1",
            Title = "Episode 1",
            Text = "This website uses Cloudflare CDN.",
        };

        var result = WebPageInteractionClassifier.Classify(snapshot);

        Assert.Equal(WebPageInteractionKind.None, result);
    }

    [Fact]
    public void Classify_PrefersChallengeOverLoginForm()
    {
        var snapshot = new WebPageSnapshot
        {
            Url = "https://example.test/login",
            HasPasswordInput = true,
            HasChallengeElement = true,
        };

        var result = WebPageInteractionClassifier.Classify(snapshot);

        Assert.Equal(WebPageInteractionKind.HumanVerification, result);
    }
}
