namespace AniMeido.App.Services;

internal sealed class StartupMigrationGate(Func<CancellationToken, Task> migrate)
{
    internal StartupMigrationGate(Func<Task> migrate) : this(_ => migrate()) { }
    internal const string BlockedMessage = "需要联网完成一次资料迁移";
    internal bool IsReady { get; private set; }
    internal bool IsRunning { get; private set; }
    internal async Task<bool> TryRunAsync(CancellationToken ct = default)
    {
        if (IsReady) return true;
        if (IsRunning) return false;
        IsRunning = true;
        try { await migrate(ct); ct.ThrowIfCancellationRequested(); IsReady = true; return true; }
#pragma warning disable CA1031 // 迁移失败保持可重试的窗口，不触发安装回滚或退出。
        catch (Exception ex) { Serilog.Log.Warning(ex, "{Message}", BlockedMessage); return false; }
#pragma warning restore CA1031
        finally { IsRunning = false; }
    }
}
