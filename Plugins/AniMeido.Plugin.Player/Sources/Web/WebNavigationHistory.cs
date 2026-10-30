namespace AniMeido.Plugin.Player.Sources.Web;

/// <summary>在浏览器线程记录导航首次归属；只过滤有旧归属证据的完成事件。</summary>
internal sealed class WebNavigationHistory
{
    private readonly Dictionary<ulong, long> _owners = [];
    private Observation? _current;
    private long _nextNumber;

    internal long BeginObservation(Action<bool, string> completed)
    {
        var number = ++_nextNumber;
        _current = new Observation(number, completed);
        return number;
    }

    internal void EndObservation(long number)
    {
        if (_current?.Number == number)
        {
            _current = null;
        }
    }

    internal void RecordStarting(ulong navigationId)
        // 同一 ID 的重定向不能把旧导航重新认作当前读取。
        => _owners.TryAdd(navigationId, _current?.Number ?? 0);

    internal void Complete(ulong navigationId, bool success, string errorStatus)
    {
        var known = _owners.Remove(navigationId, out var owner);
        var current = _current;
        if (current is null || (known && owner != current.Number))
        {
            return;
        }

        // 没观察到 Starting 就没有旧归属证据，保持原来的接纳方式。
        current.Completed(success, errorStatus);
    }

    internal void Clear()
    {
        _owners.Clear();
        _current = null;
    }

    private sealed record Observation(long Number, Action<bool, string> Completed);
}
