namespace AniMeido.App.Services;

internal enum UpdatePromptTrigger
{
    Startup,
    Manual
}

internal sealed record UpdatePromptDecision(
    bool ShouldPrompt,
    bool AllowSkip,
    bool CanResumeSkippedVersion,
    bool LogWarning);

/// <summary>纯逻辑：更新状态与触发方式决定是否提示，不读取设置也不操作 UI。</summary>
internal static class UpdatePromptPolicy
{
    public static UpdatePromptDecision Decide(
        InstalledUpdateResult result, string? skippedVersion, UpdatePromptTrigger trigger)
    {
        var manual = trigger == UpdatePromptTrigger.Manual;
        var skipped = IsSameVersion(result.LatestVersion, skippedVersion);
        return result.Status switch
        {
            InstalledUpdateStatus.UpdateAvailable => new(manual || !skipped, true, manual && skipped, false),
            InstalledUpdateStatus.PreviouslyFailed => new(manual, false, false, false),
            InstalledUpdateStatus.PendingReady or InstalledUpdateStatus.ReinstallRequired => new(true, false, false, false),
            InstalledUpdateStatus.NoUpdate => new(manual, false, false, false),
            _ => new(manual, false, false, true),
        };
    }

    public static UpdatePromptDecision Decide(
        UpdateService.UpdateCheckResult result,
        string? skippedVersion,
        UpdatePromptTrigger trigger)
    {
        var manual = trigger == UpdatePromptTrigger.Manual;
        var skipped = IsSameVersion(result.LatestVersion, skippedVersion);
        return result.Status switch
        {
            UpdateCheckStatus.UpdateAvailable => new(
                manual || !skipped, true, manual && skipped, false),
            UpdateCheckStatus.UpdateRequired => new(true, false, false, false),
            UpdateCheckStatus.NoUpdate => new(manual, false, false, false),
            _ => new(manual, false, false, true),
        };
    }

    internal static bool IsSameVersion(string? left, string? right)
    {
        if (!TryParseVersion(left, out var leftVersion)
            || !TryParseVersion(right, out var rightVersion))
            return false;

        return leftVersion == rightVersion;
    }

    // 仅兼容旧设置中的两段版本或末尾 .0；比较本身始终按 SemVer（含预览版）。
    private static bool TryParseVersion(string? value, out AniMeido.Updates.SemanticVersion? version)
    {
        if (AniMeido.Updates.SemanticVersion.TryParse(value, out version))
            return true;
        if (Version.TryParse(value, out var legacy) && legacy.Revision <= 0)
            return AniMeido.Updates.SemanticVersion.TryParse(
                $"{legacy.Major}.{legacy.Minor}.{Math.Max(0, legacy.Build)}", out version);
        return false;
    }
}
