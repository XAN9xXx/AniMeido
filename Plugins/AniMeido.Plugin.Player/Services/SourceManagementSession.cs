namespace AniMeido.Plugin.Player.Services;

/// <summary>源管理会话只拥有操作的接纳、取消和排空，不调度或串行化业务。</summary>
internal sealed class SourceManagementSession
{
    private readonly object _sync = new();
    private readonly CancellationTokenSource _readCancellation = new();
    private readonly TaskCompletionSource _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _activeOperations;
    private bool _stopping;
    private bool _hasSourceChanges;
    private Task? _stopTask;

    public bool HasSourceChanges
    {
        get
        {
            lock (_sync)
                return _hasSourceChanges;
        }
    }

    public Operation? TryBeginRead()
    {
        lock (_sync)
        {
            if (_stopping)
                return null;

            _activeOperations++;
            return new Operation(this, _readCancellation.Token);
        }
    }

    public Operation? TryBeginWrite()
    {
        lock (_sync)
        {
            if (_stopping)
                return null;

            _activeOperations++;
            return new Operation(this, CancellationToken.None);
        }
    }

    public void MarkSourcesChanged()
    {
        lock (_sync)
            _hasSourceChanges = true;
    }

    public Task StopAsync()
    {
        TaskCompletionSource completion;
        lock (_sync)
        {
            if (_stopTask is not null)
                return _stopTask;

            _stopping = true;
            completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _stopTask = completion.Task;
            if (_activeOperations == 0)
                _drained.TrySetResult();
        }

        // 先公布停止状态和任务，再在锁外取消，允许取消回调观察同一个会话。
        _ = CompleteStopAsync(completion);
        return completion.Task;
    }

    private async Task CompleteStopAsync(TaskCompletionSource completion)
    {
        try
        {
            try
            {
                await _readCancellation.CancelAsync().ConfigureAwait(false);
            }
            finally
            {
                await _drained.Task.ConfigureAwait(false);
                _readCancellation.Dispose();
            }
            completion.TrySetResult();
        }
#pragma warning disable CA1031 // 停止故障转交共享任务；取消回调失败也必须等待租约结束。
        catch (Exception ex)
        {
            completion.TrySetException(ex);
        }
#pragma warning restore CA1031
    }

    private void CompleteOperation()
    {
        lock (_sync)
        {
            _activeOperations--;
            if (_stopping && _activeOperations == 0)
                _drained.TrySetResult();
        }
    }

    internal sealed class Operation(SourceManagementSession owner, CancellationToken token) : IDisposable
    {
        private SourceManagementSession? _owner = owner;
        public CancellationToken Token { get; } = token;

        public void Dispose()
            => Interlocked.Exchange(ref _owner, null)?.CompleteOperation();
    }
}
