namespace AniMeido.PluginHost;

/// <summary>将 Host 的 UI 工作结果交还原调用方；排队成功不等于工作完成。</summary>
internal sealed class HostUiDispatcher(Func<bool> hasThreadAccess, Func<Action, bool> enqueue)
{
    public Task RunAsync(Func<Task> action)
    {
        if (hasThreadAccess())
            return action();

        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!enqueue(async () =>
        {
            try
            {
                await action();
                completion.SetResult();
            }
#pragma warning disable CA1031 // UI 队列入口只转交原异常，最终处理和日志归调用方。
            catch (Exception ex)
            {
                completion.SetException(ex);
            }
#pragma warning restore CA1031
        }))
        {
            completion.SetException(new InvalidOperationException("Host UI 已停止接收工作，操作尚未执行。"));
        }

        return completion.Task;
    }
}
