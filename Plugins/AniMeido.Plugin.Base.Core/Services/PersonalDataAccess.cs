namespace AniMeido.Plugin.Base.Services;

/// <summary>完整业务操作的准入；独占导入等待已接纳操作和读连接退出。</summary>
internal sealed class PersonalDataAccess
{
    private readonly object _sync = new();
    private readonly AsyncLocal<Operation?> _current = new();
    private Operation? _exclusive;
    private int _writers;
    private int _connections;
    private bool _recoveryFailed;
    private TaskCompletionSource? _stopped;

    internal Task StopAsync()
    {
        lock (_sync)
        {
            _stopped ??= new(TaskCreationOptions.RunContinuationsAsynchronously);
            SignalDrained();
            return _stopped.Task;
        }
    }

    internal Operation BeginWrite()
    {
        lock (_sync)
        {
            ThrowIfRecoveryFailed();
            var parent = _current.Value;
            if (parent is { IsActive: true })
            {
                parent.Root.References++;
                return _current.Value = new Operation(this, parent, parent.Root);
            }
            ObjectDisposedException.ThrowIf(_stopped is not null, this);
            if (_exclusive is not null)
                throw new PersonalDataBusyException();

            _writers++;
            return _current.Value = new Operation(this, parent);
        }
    }

    internal Operation BeginExclusive()
    {
        lock (_sync)
        {
            ThrowIfRecoveryFailed();
            if (_exclusive is not null && _current.Value is { IsActive: true } current
                && ReferenceEquals(current.Root, _exclusive))
                return BeginWrite();
            ObjectDisposedException.ThrowIf(_stopped is not null, this);
            if (_exclusive is not null || _current.Value is { IsActive: true })
                throw new PersonalDataBusyException();

            _exclusive = new Operation(this, _current.Value);
            _current.Value = _exclusive;
            SignalDrained();
            return _exclusive;
        }
    }

    internal IDisposable OpenConnection()
    {
        lock (_sync)
        {
            ThrowIfRecoveryFailed();
            var operation = _current.Value;
            ObjectDisposedException.ThrowIf(_stopped is not null && operation is not { IsActive: true }, this);
            if (_exclusive is not null)
            {
                if (operation is not { IsActive: true })
                    throw new PersonalDataBusyException();
                if (ReferenceEquals(operation.Root, _exclusive))
                {
                    if (!_exclusive.Drained.Task.IsCompletedSuccessfully)
                        throw new InvalidOperationException("独占操作必须先等待已有操作退出。");
                }
            }
            _connections++;
            return new ConnectionLease(this);
        }
    }

    private void CloseConnection()
    {
        lock (_sync)
        {
            _connections--;
            SignalDrained();
        }
    }

    private void End(Operation operation)
    {
        lock (_sync)
        {
            if (operation.Disposed)
                return;

            operation.Disposed = true;
            if (ReferenceEquals(_current.Value, operation))
                _current.Value = operation.Parent;
            if (--operation.Root.References != 0)
                return;

            if (ReferenceEquals(operation.Root, _exclusive))
                _exclusive = null;
            else
                _writers--;
            SignalDrained();
        }
    }

    private void SignalDrained()
    {
        if (_writers == 0 && _connections == 0)
        {
            _exclusive?.Drained.TrySetResult();
            if (_exclusive is null)
                _stopped?.TrySetResult();
        }
    }

    private void ThrowIfRecoveryFailed()
    {
        if (_recoveryFailed)
            throw new InvalidOperationException("数据恢复未完成，已暂停修改。请保留备份并重启后处理恢复。");
    }

    internal sealed class Operation : IDisposable
    {
        private readonly PersonalDataAccess _owner;
        internal Operation? Parent { get; }
        internal Operation Root { get; }
        internal int References = 1;
        internal bool Disposed;
        internal bool IsActive => !Disposed && Root.References > 0;
        internal TaskCompletionSource Drained { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        internal Operation(PersonalDataAccess owner, Operation? parent, Operation? root = null)
        {
            _owner = owner;
            Parent = parent;
            Root = root ?? this;
        }

        internal Task WaitForDrainAsync(CancellationToken cancellationToken)
            => Root.Drained.Task.WaitAsync(cancellationToken);

        internal void MarkRecoveryFailed()
        {
            lock (_owner._sync)
                _owner._recoveryFailed = true;
        }

        public void Dispose() => _owner.End(this);
    }

    private sealed class ConnectionLease(PersonalDataAccess owner) : IDisposable
    {
        private PersonalDataAccess? _owner = owner;
        public void Dispose() => Interlocked.Exchange(ref _owner, null)?.CloseConnection();
    }
}

internal sealed class PersonalDataBusyException()
    : InvalidOperationException("正在导入或恢复档案，请稍后重试。");
