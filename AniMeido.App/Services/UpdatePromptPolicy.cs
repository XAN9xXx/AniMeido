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
        if (!Version.TryParse(left, out var leftVersion)
            || !Version.TryParse(right, out var rightVersion))
            return false;

        // 清单常用三段版本，程序集常用四段版本；缺省的 Build/Revision 按 0 比较。
        return Normalize(leftVersion) == Normalize(rightVersion);
    }

    private static Version Normalize(Version version)
        => new(version.Major, version.Minor, Math.Max(0, version.Build), Math.Max(0, version.Revision));
}
