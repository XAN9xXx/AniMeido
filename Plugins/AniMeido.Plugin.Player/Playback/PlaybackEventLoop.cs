namespace AniMeido.Plugin.Player.Playback;

/// <summary>事件线程拥有原生资源的最后一次使用及释放；等待超时不转移所有权。</summary>
internal sealed class PlaybackEventLoop
{
    private readonly Action _readEvent;
    private readonly Action _releaseResources;
    private readonly TaskCompletionSource _stopped = new(
        TaskCreationOptions.RunContinuationsAsynchronously);
    private volatile bool _stopRequested;

    public PlaybackEventLoop(Action readEvent, Action releaseResources)
    {
        _readEvent = readEvent;
        _releaseResources = releaseResources;
        new Thread(Run)
        {
            IsBackground = true,
            Name = "AniMeido libmpv events",
        }.Start();
    }

    public Task StopAsync()
    {
        _stopRequested = true;
        return _stopped.Task;
    }

    private void Run()
    {
        Exception? failure = null;
        try
        {
            while (!_stopRequested)
            {
                _readEvent();
            }
        }
#pragma warning disable CA1031 // 专用线程把故障交回资源所有者，不让异常逃逸进程。
        catch (Exception ex)
        {
            failure = ex;
        }
#pragma warning restore CA1031
        finally
        {
            try
            {
                _releaseResources();
            }
#pragma warning disable CA1031 // 保留读取和释放的原始故障，由关闭调用方处理。
            catch (Exception ex)
            {
                failure = failure is null ? ex : new AggregateException(failure, ex);
            }
#pragma warning restore CA1031

            if (failure is null)
            {
                _stopped.TrySetResult();
            }
            else
            {
                _stopped.TrySetException(failure);
            }
        }
    }
}
