namespace AniMeido.Plugin.Chat.Models;

/// <summary>
/// 聊天室窗口设置。当前管理窗口级透明度百分比。
/// 不持久化，关闭后恢复默认值。
/// </summary>
public sealed class ChatWindowSettings
{
    private int _windowOpacityPercent = 100;

    /// <summary>
    /// 窗口透明度百分比，范围 40~100。默认 100（完全不透明）。
    /// 对应 Win32 SetLayeredWindowAttributes alpha 值（102~255）。
    /// </summary>
    public int WindowOpacityPercent
    {
        get => _windowOpacityPercent;
        set => _windowOpacityPercent = Math.Clamp(value, 40, 100);
    }

    /// <summary>用于显示的透明度文本，如 "100%"、"60%"。</summary>
    public string WindowOpacityText => $"{_windowOpacityPercent}%";
}
