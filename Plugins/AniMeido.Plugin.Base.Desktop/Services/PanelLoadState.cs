namespace AniMeido.Plugin.Base.Services;

internal enum ArchivePanelKind
{
    Archives,
    Statistics,
    Review,
    Screenshots,
}

/// <summary>
/// Small state machine for one independently loaded page panel.
/// Generation prevents a late result from an invalidated request becoming
/// visible; an in-flight task is shared by concurrent callers.
/// </summary>
internal sealed class PanelLoadState
{
    private Task? _inflight;
    private CancellationTokenSource? _requestCancellation;

    public bool IsLoaded { get; private set; }

    public bool IsDirty { get; private set; } = true;

    public int Generation { get; private set; }

    public bool IsLoading => _inflight is not null;

    public Task EnsureAsync(
        Func<int, CancellationToken, Task> loader,
        CancellationToken lifetimeCancellation)
    {
        if (IsLoaded && !IsDirty)
        {
            return Task.CompletedTask;
        }

        if (_inflight is not null)
        {
            return _inflight;
        }

        var generation = ++Generation;
        var requestCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(
                lifetimeCancellation);
        _requestCancellation = requestCancellation;
        var completion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        _inflight = completion.Task;
        _ = LoadCoreAsync(
            generation,
            requestCancellation,
            loader,
            completion);
        return completion.Task;
    }

    public bool IsCurrent(int generation, CancellationToken cancellationToken)
        => generation == Generation && !cancellationToken.IsCancellationRequested;

    public void Invalidate()
    {
        IsLoaded = false;
        IsDirty = true;
        Generation++;
        _requestCancellation?.Cancel();
        _requestCancellation = null;
        _inflight = null;
    }

    public void CancelInflight()
    {
        Generation++;
        _requestCancellation?.Cancel();
        _requestCancellation = null;
        _inflight = null;
    }

    private async Task LoadCoreAsync(
        int generation,
        CancellationTokenSource requestCancellation,
        Func<int, CancellationToken, Task> loader,
        TaskCompletionSource completion)
    {
        try
        {
            await loader(generation, requestCancellation.Token);
            if (IsCurrent(generation, requestCancellation.Token))
            {
                IsLoaded = true;
                IsDirty = false;
            }
            completion.TrySetResult();
        }
        catch (OperationCanceledException)
            when (requestCancellation.IsCancellationRequested)
        {
            completion.TrySetCanceled(requestCancellation.Token);
        }
#pragma warning disable CA1031 // The TCS is the deliberate async error boundary.
        catch (Exception ex)
        {
            completion.TrySetException(ex);
        }
#pragma warning restore CA1031
        finally
        {
            if (generation == Generation)
            {
                _inflight = null;
                _requestCancellation = null;
            }

            requestCancellation.Dispose();
        }
    }
}
