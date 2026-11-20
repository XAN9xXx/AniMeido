namespace AniMeido.Plugin.Base.Services;

// 保留所有尚未结束的页面操作；重复点击返回的空任务不会盖掉已接纳保存。
internal sealed class DesktopPageOperations
{
    internal Task Pending { get; private set; } = Task.CompletedTask;

    internal void Track(Task task)
    {
        DesktopUiWork.Track(task);
        if (ReferenceEquals(task, Pending)) return;
        if (Pending.IsCompleted) Pending = task;
        else if (!task.IsCompletedSuccessfully) Pending = Task.WhenAll(Pending, task);
    }

    internal async Task DrainAsync()
    {
        Task batch;
        do { batch = Pending; await batch; } while (!ReferenceEquals(batch, Pending));
    }
}
