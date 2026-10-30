namespace AniMeido.Tests;

/// <summary>暂存 UI 续体；失败收尾时恢复调度，避免把测试失败变成遗留任务。</summary>
internal sealed class PausedUiContext : SynchronizationContext
{
    private readonly object _gate = new();
    private readonly List<(SendOrPostCallback Callback, object? State)> _pending = [];
    private readonly TaskCompletionSource _firstPost = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _resumed;
    private int _postCount;
    public int PostCount => Volatile.Read(ref _postCount);
    public Task FirstPost => _firstPost.Task;

    public override void Post(SendOrPostCallback callback, object? state)
    {
        lock (_gate)
        {
            _postCount++;
            if (!_resumed)
            {
                _pending.Add((callback, state));
                _firstPost.TrySetResult();
                return;
            }
        }
        ThreadPool.QueueUserWorkItem(_ => callback(state));
        _firstPost.TrySetResult();
    }

    public void Resume()
    {
        List<(SendOrPostCallback Callback, object? State)> pending;
        lock (_gate)
        {
            _resumed = true;
            pending = [.. _pending];
            _pending.Clear();
        }
        foreach (var (callback, state) in pending)
            ThreadPool.QueueUserWorkItem(_ => callback(state));
    }
}
