using AniMeido.Plugin.Chat.Views;

namespace AniMeido.Plugin.Chat.Services;

/// <summary>
/// 管理聊天室窗口生命周期。负责创建、激活、防止重复打开和关闭清理。
/// 属于插件内部实现，不对外暴露。
/// </summary>
public sealed class ChatWindowManager
{
    private ChatWindow? _chatWindow;
    private bool _isShuttingDown;

    public ChatWindowManager()
    {
        // 订阅应用关闭通知，确保 ChatWindow 在 ServiceProvider 释放前关闭
        AniMeido.Contracts.AppServices.Closing += OnAppClosing;
    }

    /// <summary>
    /// 打开或激活聊天室窗口。如果窗口已存在则激活已有窗口，否则创建新窗口。
    /// 应用退出阶段不执行任何操作。
    /// </summary>
    public void OpenOrActivate()
    {
        if (_isShuttingDown)
            return;

        if (_chatWindow != null)
        {
            // 窗口已存在，尝试激活
            try
            {
                _chatWindow.Activate();
            }
            catch
            {
                // 窗口可能已销毁但事件未触发，重新创建
                _chatWindow = null;
                CreateAndShowWindow();
            }
            return;
        }

        CreateAndShowWindow();
    }

    /// <summary>
    /// 如果聊天室窗口已打开，关闭并清理引用。
    /// 应用退出或主窗口关闭时调用，不触发 Activate 等 UI 操作。
    /// </summary>
    public void CloseIfOpen()
    {
        _isShuttingDown = true;

        if (_chatWindow == null)
        {
            System.Diagnostics.Debug.WriteLine("[ChatWindowManager] CloseIfOpen: no window to close");
            return;
        }

        System.Diagnostics.Debug.WriteLine("[ChatWindowManager] CloseIfOpen: closing chat window");
        // 取消订阅再关闭，避免 Closed 事件中重新创建或遗留引用
        _chatWindow.Closed -= OnChatWindowClosed;
        _chatWindow.Close();
        _chatWindow = null;
        System.Diagnostics.Debug.WriteLine("[ChatWindowManager] CloseIfOpen: window closed and reference cleared");
    }

    private void OnAppClosing()
    {
        CloseIfOpen();
    }

    private void CreateAndShowWindow()
    {
        var window = new ChatWindow();
        window.Closed += OnChatWindowClosed;
        _chatWindow = window;
        window.Activate();
    }

    private void OnChatWindowClosed(object sender, Microsoft.UI.Xaml.WindowEventArgs args)
    {
        if (sender is ChatWindow window)
        {
            window.Closed -= OnChatWindowClosed;
        }
        _chatWindow = null;
    }
}
