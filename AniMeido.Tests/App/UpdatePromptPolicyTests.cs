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

    [Theory]
    [InlineData((int)InstalledUpdateStatus.NoUpdate, false, false, false)]
    [InlineData((int)InstalledUpdateStatus.NoUpdate, true, true, false)]
    [InlineData((int)InstalledUpdateStatus.UpdateAvailable, false, false, false)]
    [InlineData((int)InstalledUpdateStatus.UpdateAvailable, true, true, false)]
    [InlineData((int)InstalledUpdateStatus.PendingReady, false, true, false)]
    [InlineData((int)InstalledUpdateStatus.PendingReady, true, true, false)]
    [InlineData((int)InstalledUpdateStatus.PreviouslyFailed, false, false, false)]
    [InlineData((int)InstalledUpdateStatus.PreviouslyFailed, true, true, false)]
    [InlineData((int)InstalledUpdateStatus.ReinstallRequired, false, true, false)]
    [InlineData((int)InstalledUpdateStatus.ReinstallRequired, true, true, false)]
    [InlineData((int)InstalledUpdateStatus.NetworkError, false, false, true)]
    [InlineData((int)InstalledUpdateStatus.NetworkError, true, true, true)]
    [InlineData((int)InstalledUpdateStatus.SignatureInvalid, false, false, true)]
    [InlineData((int)InstalledUpdateStatus.SignatureInvalid, true, true, true)]
    [InlineData((int)InstalledUpdateStatus.InvalidManifest, false, false, true)]
    [InlineData((int)InstalledUpdateStatus.InvalidManifest, true, true, true)]
    public void InstalledResultsRespectTrigger(int statusValue, bool manual, bool prompt, bool warning)
    {
        var status = (InstalledUpdateStatus)statusValue;
        var result = new InstalledUpdateResult(status, PendingVersion: "2.4.0-preview.1");
        var decision = UpdatePromptPolicy.Decide(result, "2.4.0-preview.1",
            manual ? UpdatePromptTrigger.Manual : UpdatePromptTrigger.Startup);
        Assert.Equal(prompt, decision.ShouldPrompt);
        Assert.Equal(warning, decision.LogWarning);
        Assert.Equal(status == InstalledUpdateStatus.UpdateAvailable, decision.AllowSkip);
        Assert.Equal(status == InstalledUpdateStatus.UpdateAvailable && manual, decision.CanResumeSkippedVersion);
    }

    [Theory]
    [InlineData("2.4.0-preview.1", "2.4.0-preview.1+commit", true)]
    [InlineData("2.4.0-preview.1", "2.4.0-preview.2", false)]
    [InlineData("2.4.0-preview.1", "2.4.0", false)]
    public void PreviewSkipUsesSemver(string left, string right, bool same)
        => Assert.Equal(same, UpdatePromptPolicy.IsSameVersion(left, right));

    [Theory]
    [InlineData(null)]
    [InlineData("2.4.0-preview.0")]
    [InlineData("2.4.0-preview.2")]
    public void InstalledAvailablePromptsUnlessThatExactVersionWasSkipped(string? skipped)
    {
        var result = new InstalledUpdateResult(InstalledUpdateStatus.UpdateAvailable, PendingVersion: "2.4.0-preview.1");
        var decision = UpdatePromptPolicy.Decide(result, skipped, UpdatePromptTrigger.Startup);
        Assert.True(decision.ShouldPrompt);
        Assert.True(decision.AllowSkip);
        Assert.False(decision.CanResumeSkippedVersion);
    }
}
