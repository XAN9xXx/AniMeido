namespace AniMeido.App.Services;

internal sealed class StartupMigrationGate(Func<CancellationToken, Task> migrate)
{
    internal StartupMigrationGate(Func<Task> migrate) : this(_ => migrate()) { }

    internal bool IsReady { get; private set; }

    internal bool IsRunning { get; private set; }

    internal async Task<bool> TryRunAsync(CancellationToken ct = default)
    {
        if (IsReady)
            return true;
        if (IsRunning)
            return false;

        IsRunning = true;
        try
        {
            await migrate(ct);
            ct.ThrowIfCancellationRequested();
            IsReady = true;
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return false;
        }
#pragma warning disable CA1031 // 迁移操作记录失败；这里仅保留可重试的窗口，不重复记录。
        catch (Exception)
        {
            return false;
        }
#pragma warning restore CA1031
        finally
        {
            IsRunning = false;
        }
    }
}
