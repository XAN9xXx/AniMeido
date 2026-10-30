namespace AniMeido.App.Services;

/// <summary>拥有一轮托盘的准备、停止和回调资格；原生资源仍由消息线程释放。</summary>
internal sealed class TrayIconSession(Action open, Action exit, Action unavailable)
{
    private readonly object _sync = new();
    private readonly TaskCompletionSource _ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _stopRequested;
    private bool _ended;
    private bool _unavailableNotified;
    private Exception? _failure;

    internal Task Ready => _ready.Task;
    internal Task Stopped => _stopped.Task;

    internal bool StopRequested
    {
        get
        {
            lock (_sync)
                return _stopRequested;
        }
    }

    internal bool IsRunning
    {
        get
        {
            lock (_sync)
                return IsRunningCore;
        }
    }

    internal Exception? Failure
    {
        get
        {
            lock (_sync)
                return _failure;
        }
    }

    private bool IsRunningCore => _ready.Task.IsCompletedSuccessfully
        && !_stopRequested && !_ended && _failure is null;

    internal void MarkReady()
    {
        lock (_sync)
        {
            if (!_stopRequested && !_ended && _failure is null)
                _ready.TrySetResult();
        }
    }

    internal void RequestStop()
    {
        lock (_sync)
            _stopRequested = true;
    }

    internal void RecordFailure(Exception exception)
    {
        lock (_sync)
            _failure ??= exception;
    }

    internal bool Complete()
    {
        lock (_sync)
        {
            if (_ended)
                return false;

            _ended = true;
            if (!_ready.Task.IsCompleted)
            {
                if (_failure is not null)
                    _ready.TrySetException(new InvalidOperationException("无法创建 AniMeido 托盘图标。", _failure));
                else
                    _ready.TrySetCanceled();
            }

            _stopped.TrySetResult();
            return _ready.Task.IsCompletedSuccessfully && !_stopRequested;
        }
    }

    internal void InvokeOpen() => Invoke(open);

    internal void InvokeExit() => Invoke(exit);

    private void Invoke(Action action)
    {
        lock (_sync)
        {
            if (!IsRunningCore)
                return;
        }

        action();
    }

    internal void NotifyUnavailable()
    {
        lock (_sync)
        {
            if (!_ended || !_ready.Task.IsCompletedSuccessfully || _stopRequested || _unavailableNotified)
                return;

            _unavailableNotified = true;
        }

        unavailable();
    }
}
