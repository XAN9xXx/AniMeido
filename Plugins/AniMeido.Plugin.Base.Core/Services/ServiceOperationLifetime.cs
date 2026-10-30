namespace AniMeido.Plugin.Base.Services;

/// <summary>服务各自持有的操作生命周期；只负责停止接纳、取消与排空，不调度或串行化业务。</summary>
internal sealed class ServiceOperationLifetime
{
    private readonly object _gate = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly TaskCompletionSource _drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _activeOperations;
    private bool _stopping;
    private Task? _stopTask;

    public Operation Begin(CancellationToken cancellationToken = default)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_stopping, this);
            cancellationToken.ThrowIfCancellationRequested();
            var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
            _activeOperations++;
            return new Operation(this, cancellation);
        }
    }

    public ValueTask StopAsync(Action? releaseResources = null)
    {
        lock (_gate)
        {
            if (_stopTask is not null)
                return new ValueTask(_stopTask);

            _stopping = true;
            if (_activeOperations == 0)
                _drained.TrySetResult();
            _stopTask = StopCoreAsync(releaseResources);
            return new ValueTask(_stopTask);
        }
    }

    private async Task StopCoreAsync(Action? releaseResources)
    {
        try
        {
            await _shutdown.CancelAsync().ConfigureAwait(false);
        }
        finally
        {
            await _drained.Task.ConfigureAwait(false);
            _shutdown.Dispose();
            releaseResources?.Invoke();
        }
    }

    private void Complete()
    {
        lock (_gate)
        {
            _activeOperations--;
            if (_stopping && _activeOperations == 0)
                _drained.TrySetResult();
        }
    }

    internal sealed class Operation(ServiceOperationLifetime owner, CancellationTokenSource cancellation) : IDisposable
    {
        private ServiceOperationLifetime? _owner = owner;
        public CancellationToken Token => cancellation.Token;

        public void Dispose()
        {
            var current = Interlocked.Exchange(ref _owner, null);
            if (current is null)
                return;

            cancellation.Dispose();
            current.Complete();
        }
    }
}
