namespace AniMeido.App.Services;

/// <summary>
/// 导航返回栈条目：保存页面实例（object 类型，运行时由使用方保证是 Page）及其原始导航参数。
/// 使用 object 而非 Page 避免 WinUI 类型依赖，以便在非 UI 测试环境中运行。
/// </summary>
internal sealed record NavigationEntry(object Page, object? Parameter);

internal sealed class NavigationStack
{
    private readonly Stack<NavigationEntry> _stack = new();
    private const int MaxDepth = 20;

    public int Count => _stack.Count;
    public bool CanGoBack => _stack.Count > 0;

    public NavigationEntry? Peek() => _stack.Count > 0 ? _stack.Peek() : null;

    /// <summary>压入条目。如果栈顶是重复页面（相同对象引用），跳过。</summary>
    public void Push(object page, object? parameter)
    {
        if (_stack.Count > 0)
        {
            var top = _stack.Peek();
            if (top.Page == page)
                return;
        }
        _stack.Push(new NavigationEntry(page, parameter));

        // 超出最大深度时移除最旧条目
        while (_stack.Count > MaxDepth && _stack.Count > 0)
        {
            var entries = _stack.ToArray();
            _stack.Clear();
            for (int i = entries.Length - 2; i >= 0; i--)
                _stack.Push(entries[i]);
        }
    }

    public NavigationEntry Pop()
    {
        if (_stack.Count == 0)
            throw new InvalidOperationException("返回栈为空");
        return _stack.Pop();
    }

    public void Clear() => _stack.Clear();
}
