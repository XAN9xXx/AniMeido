using Microsoft.Extensions.Logging;

namespace AniMeido.App.Helpers;

public static class TaskExtensions
{
    /// <summary>
    /// 统一观察后台任务异常（带 ILogger 版本）。
    /// 任务内部预期的 OperationCanceledException 会被静默处理。
    /// </summary>
    public static void Forget(this Task task, ILogger logger, string operationName)
    {
        _ = ObserveAsync(task, logger, operationName);
    }

    /// <summary>
    /// 统一观察后台任务异常（无 ILogger 版本，用于页面层）。
    /// 任务内部预期的 OperationCanceledException 会被静默处理。
    /// 异常通过 Debug.WriteLine 输出。
    /// </summary>
    public static void Forget(this Task task, string operationName)
    {
        _ = ObserveAsync(task, operationName);
    }

    private static async Task ObserveAsync(Task task, ILogger logger, string operationName)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 预期取消，不记录
        }
#pragma warning disable CA1031 // fire-and-forget 需要捕获所有异常避免静默丢失
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Fire-and-forget task failed: {OperationName}", operationName);
        }
#pragma warning restore CA1031
    }

    private static async Task ObserveAsync(Task task, string operationName)
    {
        try
        {
            await task.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // 预期取消，不记录
        }
#pragma warning disable CA1031 // fire-and-forget 需要捕获所有异常避免静默丢失
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Forget] {operationName} failed: {ex.Message}");
        }
#pragma warning restore CA1031
    }
}
