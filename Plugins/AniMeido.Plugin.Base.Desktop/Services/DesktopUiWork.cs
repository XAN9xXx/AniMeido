namespace AniMeido.Plugin.Base.Services;

// 关闭窗口时等待界面已接纳的任务，再封住数据库。集合只保留尚未结束的任务。
public static class DesktopUiWork
{
    private static readonly object Sync = new();
    private static readonly HashSet<Task> Pending = [];

    public static void Track(Task task)
    {
        if (task.IsCompleted) return;
        lock (Sync) if (!Pending.Add(task)) return;
        _ = task.ContinueWith(static completed => { lock (Sync) Pending.Remove(completed); },
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    public static async Task DrainAsync()
    {
        List<Exception>? failures = null;
        while (true)
        {
            Task[] tasks;
            lock (Sync) tasks = Pending.ToArray();
            if (tasks.Length == 0) break;
            var batch = Task.WhenAll(tasks);
            try { await batch; }
#pragma warning disable CA1031 // 等完同批任务后继续等待后来接纳的任务，最后统一报告失败。
            catch (Exception error) { (failures ??= []).Add(batch.Exception ?? error); }
#pragma warning restore CA1031
        }
        if (failures is not null) throw new AggregateException(failures).Flatten();
    }
}
