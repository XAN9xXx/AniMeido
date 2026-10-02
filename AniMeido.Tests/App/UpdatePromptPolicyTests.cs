using AniMeido.App.Services;

namespace AniMeido.Tests;

public sealed class UpdatePromptPolicyTests
{
    [Theory]
    [InlineData(UpdateCheckStatus.UpdateAvailable, null, false, true, true, false, false)]
    [InlineData(UpdateCheckStatus.UpdateAvailable, "2.2.0", false, false, true, false, false)]
    [InlineData(UpdateCheckStatus.UpdateAvailable, "2.1.0", false, true, true, false, false)]
    [InlineData(UpdateCheckStatus.UpdateAvailable, "2.3.0", false, true, true, false, false)]
    [InlineData(UpdateCheckStatus.UpdateAvailable, "invalid", false, true, true, false, false)]
    [InlineData(UpdateCheckStatus.UpdateRequired, null, false, true, false, false, false)]
    [InlineData(UpdateCheckStatus.UpdateRequired, "2.2.0", false, true, false, false, false)]
    [InlineData(UpdateCheckStatus.NoUpdate, null, false, false, false, false, false)]
    [InlineData(UpdateCheckStatus.NoUpdate, "2.2.0", false, false, false, false, false)]
    [InlineData(UpdateCheckStatus.NetworkError, null, false, false, false, false, true)]
    [InlineData(UpdateCheckStatus.InvalidManifest, null, false, false, false, false, true)]
    [InlineData(UpdateCheckStatus.UnknownCurrentVersion, null, false, false, false, false, true)]
    [InlineData(UpdateCheckStatus.UpdateAvailable, null, true, true, true, false, false)]
    [InlineData(UpdateCheckStatus.UpdateAvailable, "2.2.0", true, true, true, true, false)]
    [InlineData(UpdateCheckStatus.UpdateAvailable, "2.2.0.0", true, true, true, true, false)]
    [InlineData(UpdateCheckStatus.UpdateAvailable, "2.1.0", true, true, true, false, false)]
    [InlineData(UpdateCheckStatus.UpdateRequired, null, true, true, false, false, false)]
    [InlineData(UpdateCheckStatus.UpdateRequired, "2.2.0", true, true, false, false, false)]
    [InlineData(UpdateCheckStatus.NoUpdate, null, true, true, false, false, false)]
    [InlineData(UpdateCheckStatus.NetworkError, null, true, true, false, false, true)]
    [InlineData(UpdateCheckStatus.InvalidManifest, null, true, true, false, false, true)]
    [InlineData(UpdateCheckStatus.UnknownCurrentVersion, null, true, true, false, false, true)]
    public void Decide_UsesStatusSkippedVersionAndTrigger(
        UpdateCheckStatus status,
        string? skippedVersion,
        bool manual,
        bool shouldPrompt,
        bool allowSkip,
        bool canResumeSkippedVersion,
        bool logWarning)
    {
        var result = new UpdateService.UpdateCheckResult(status, "2.2.0", "https://example.invalid/update.exe", "更新说明");

        var decision = UpdatePromptPolicy.Decide(result, skippedVersion,
            manual ? UpdatePromptTrigger.Manual : UpdatePromptTrigger.Startup);

        Assert.Equal(shouldPrompt, decision.ShouldPrompt);
        Assert.Equal(allowSkip, decision.AllowSkip);
        Assert.Equal(canResumeSkippedVersion, decision.CanResumeSkippedVersion);
        Assert.Equal(logWarning, decision.LogWarning);
    }

    [Theory]
    [InlineData("2.2.0", "2.2.0", true)]
    [InlineData("2.2.0", "2.2.0.0", true)]
    [InlineData("2.2", "2.2.0", true)]
    [InlineData("2.2.0", "2.2.0.1", false)]
    [InlineData("2.3.0", "2.2.0", false)]
    [InlineData("2.1.0", "2.2.0", false)]
    [InlineData(null, "2.2.0", false)]
    [InlineData("2.2.0", null, false)]
    [InlineData("invalid", "invalid", false)]
    public void IsSameVersion_OnlyMatchesEquivalentVersions(string? left, string? right, bool expected)
        => Assert.Equal(expected, UpdatePromptPolicy.IsSameVersion(left, right));

    [Theory]
    [InlineData("2.2.0", false)]
    [InlineData("2.3.0", true)]
    [InlineData("2.4.0", true)]
    public void Decide_SkippingOneRelease_DoesNotSuppressLaterReleases(string latest, bool shouldPrompt)
    {
        var result = new UpdateService.UpdateCheckResult(UpdateCheckStatus.UpdateAvailable, latest, null, null);

        var decision = UpdatePromptPolicy.Decide(result, "2.2.0", UpdatePromptTrigger.Startup);

        Assert.Equal(shouldPrompt, decision.ShouldPrompt);
    }
}
