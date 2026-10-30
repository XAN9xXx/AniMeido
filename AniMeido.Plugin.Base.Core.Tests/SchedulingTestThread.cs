namespace AniMeido.Tests;

internal static class SchedulingTestThread
{
    // 独立线程用于观察调用是否同步阻塞，内层任务仍由业务测试等待。
    internal static Task<T> InvokeAsync<T>(Func<T> operation, SynchronizationContext context)
        => Task.Factory.StartNew(() =>
        {
            var previous = SynchronizationContext.Current;
            try
            {
                SynchronizationContext.SetSynchronizationContext(context);
                return operation();
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(previous);
            }
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
}
