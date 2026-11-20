namespace AniMeido.App.Services;

public enum DesktopVisualStyle { Fluent10, Fluent11, Paper }

public enum DesktopFontStyle { System = 0, Serif = 1, Classic = 2 }

public enum DesktopCatalogCardStyle { Cover, Detailed }

public sealed record DesktopAppearance(
    DesktopVisualStyle VisualStyle = DesktopVisualStyle.Fluent11,
    DesktopFontStyle FontStyle = DesktopFontStyle.System,
    DesktopCatalogCardStyle CatalogCards = DesktopCatalogCardStyle.Cover,
    bool ShowPlanReminders = false);

public sealed class DesktopAppearanceEditor(
    DesktopAppearance initial,
    Func<DesktopAppearance, CancellationToken, Task> save)
{
    private Task _tail = Task.CompletedTask;
    private bool _stopping;
    private DesktopAppearance _lastSaved = initial;

    public DesktopAppearance Current { get; private set; } = initial;

    public bool HasUnsavedChanges => Current != _lastSaved;

    public event EventHandler<DesktopAppearance>? Changed;

    public Task UpdateAsync(
        Func<DesktopAppearance, DesktopAppearance> update,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(update);
        ObjectDisposedException.ThrowIf(_stopping, this);
        if (cancellationToken.IsCancellationRequested)
            return Task.FromCanceled(cancellationToken);
        var next = update(Current);
        if (next == Current)
            return Task.CompletedTask;
        return QueueSaveAsync(next, publish: true, cancellationToken);
    }

    public Task RetrySaveAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_stopping, this);
        if (cancellationToken.IsCancellationRequested)
            return Task.FromCanceled(cancellationToken);
        return QueueSaveAsync(Current, publish: false, cancellationToken);
    }

    private Task QueueSaveAsync(
        DesktopAppearance snapshot,
        bool publish,
        CancellationToken cancellationToken)
    {
        var previous = _tail;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _tail = completion.Task;
        return SaveCoreAsync(previous, completion, snapshot, publish, cancellationToken);
    }

    public Task StopAsync()
    {
        _stopping = true;
        return _tail;
    }

    private async Task SaveCoreAsync(
        Task previous,
        TaskCompletionSource completion,
        DesktopAppearance snapshot,
        bool publish,
        CancellationToken cancellationToken)
    {
        try
        {
            if (publish)
            {
                Current = snapshot;
                Changed?.Invoke(this, snapshot);
            }
            await previous;
            cancellationToken.ThrowIfCancellationRequested();
            await save(snapshot, cancellationToken);
            _lastSaved = snapshot;
        }
        finally
        {
            completion.TrySetResult();
        }
    }
}
