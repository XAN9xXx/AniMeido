namespace AniMeido.Updates.Install;

public enum RollbackChoice
{
    Restore,
    Keep,
}

public sealed record LaunchDecision(InstallState State, bool NeedsRollbackChoice, string[] Messages);

public sealed record RollbackRequest(string Version, string Previous, string Backup, string ArchiveDirectory);

/// <summary>纯状态转移；恢复数据及最终落盘由 InstallManager 在锁内执行。</summary>
public static class LaunchDecisions
{
    public static LaunchDecision Evaluate(InstallState state, Func<string, bool> isValidVersion,
        RollbackChoice? choice = null)
    {
        state.Validate();
        var messages = new List<string>();
        if (state.Pending is { } pending)
        {
            if (isValidVersion(pending))
            {
                state = state with
                {
                    Previous = state.Current,
                    Current = pending,
                    Pending = null,
                    CurrentHealthy = false,
                    FailedStarts = 0,
                    Migration = null,
                };
                messages.Add($"切换到待启动版本 {pending}。");
            }
            else
            {
                state = state with { Pending = null };
                messages.Add($"待切换版本 {pending} 无效，已清除 pending。");
            }
        }

        if (!state.CurrentHealthy && state.FailedStarts >= 2)
        {
            if (state.Previous is { } previous && isValidVersion(previous))
            {
                var needsRestore = state.Migration?.Version == state.Current;
                if (needsRestore && choice is null)
                {
                    return new LaunchDecision(state, true, messages.ToArray());
                }
                if (needsRestore && choice == RollbackChoice.Keep)
                {
                    state = state with { FailedStarts = 0 };
                    messages.Add("用户保留新版本，重新给予两次启动机会。");
                }
                else
                {
                    var failed = state.Current;
                    state = state with
                    {
                        Current = previous,
                        Previous = null,
                        CurrentHealthy = true,
                        FailedStarts = 0,
                        Migration = null,
                        FailedVersions = state.FailedVersions.Append(failed).Distinct(StringComparer.Ordinal).ToArray(),
                        RolledBackFrom = new RolledBackNotice
                        {
                            Version = failed,
                            Reason = needsRestore ? RolledBackNotice.UserRestored : RolledBackNotice.StartFailed,
                        },
                    };
                    messages.Add($"从 {failed} 恢复到 {previous}。");
                }
            }
            else
            {
                messages.Add("当前版本连续启动失败，但没有有效的上一版本，继续启动当前版本。");
            }
        }

        if (!state.CurrentHealthy)
        {
            // 长期没有可回滚版本时避免整数溢出。
            state = state with { FailedStarts = state.FailedStarts == int.MaxValue ? int.MaxValue : state.FailedStarts + 1 };
        }
        return new LaunchDecision(state, false, messages.ToArray());
    }
}
