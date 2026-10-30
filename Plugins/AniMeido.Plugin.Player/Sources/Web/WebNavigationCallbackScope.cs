namespace AniMeido.Plugin.Player.Sources.Web;

internal sealed class WebNavigationCallbackScope
{
    private readonly object _gate = new();
    private readonly TaskCompletionSource _drained =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _accepting = true;
    private int _activeCallbacks;

    internal bool IsAccepting
    {
        get
        {
            lock (_gate)
            {
                return _accepting;
            }
        }
    }

    internal IDisposable? TryEnter()
    {
        lock (_gate)
        {
            if (!_accepting)
            {
                return null;
            }

            _activeCallbacks++;
            return new Registration(this);
        }
    }

    internal void StopAccepting()
    {
        lock (_gate)
        {
            _accepting = false;
            if (_activeCallbacks == 0)
            {
                _drained.TrySetResult();
            }
        }
    }

    internal Task WaitForDrainAsync() => _drained.Task;

    private void Exit()
    {
        lock (_gate)
        {
            _activeCallbacks--;
            if (!_accepting && _activeCallbacks == 0)
            {
                _drained.TrySetResult();
            }
        }
    }

    private sealed class Registration(WebNavigationCallbackScope owner) : IDisposable
    {
        private WebNavigationCallbackScope? _owner = owner;

        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Exit();
    }
}
