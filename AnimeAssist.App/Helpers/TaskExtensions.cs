using Microsoft.Extensions.Logging;

namespace AniMeido.App.Helpers;

/// <summary>
/// 安全执行 fire-and-forget 异步任务的扩展方法。
/// 所有 `_ = SomeAsync()` 应替换为 `SomeAsync().Forget(...)`。
/// </summary>
public static class TaskExtensions
{
    /// <summary>
    /// 启动后台任务并统一观察异常（带 ILogger 版本）。
    /// 任务内部预期的 OperationCanceledException 会被静默处理。
    /// </summary>
    /// <param name="task">要执行的后台任务。</param>
    /// <param name="logger">日志记录器，用于记录未观察异常。</param>
    /// <param name="operationName">操作名称，用于日志标识。</param>
    public static void Forget(this Task task, ILogger logger, string operationName)
    {
        _ = ObserveAsync(task, logger, operationName);
    }

    /// <summary>
    /// 启动后台任务并统一观察异常（无 ILogger 版本，用于页面层）。
    /// 任务内部预期的 OperationCanceledException 会被静默处理。
    /// 异常通过 Debug.WriteLine 输出。
    /// </summary>
    /// <param name="task">要执行的后台任务。</param>
    /// <param name="operationName">操作名称，用于日志标识。</param>
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
            logger.LogError(ex, "Fire-and-forget task failed: {OperationName}", operationName);
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
