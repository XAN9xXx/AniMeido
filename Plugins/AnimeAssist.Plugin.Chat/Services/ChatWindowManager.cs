using AniMeido.Contracts;
using AniMeido.Plugin.Chat.Views;
using Microsoft.UI.Xaml;
using System.Runtime.InteropServices;

namespace AniMeido.Plugin.Chat.Services;

/// <summary>
/// Owns the optional chat window without exposing it to the host.
/// </summary>
public sealed class ChatWindowManager
{
    private ChatWindow? _chatWindow;
    private Window? _mainWindow;
    private bool _isAppClosing;

    public void OpenOrActivate()
    {
        if (_isAppClosing)
        {
            return;
        }

        if (_chatWindow is not null)
        {
            try
            {
                _chatWindow.Activate();
                return;
            }
            catch (COMException)
            {
                DetachWindow();
            }
        }

        _mainWindow = AppServices.MainWindow as Window;
        if (_mainWindow is not null)
        {
            _mainWindow.Closed += OnMainWindowClosed;
        }

        _chatWindow = new ChatWindow();
        _chatWindow.Closed += OnChatWindowClosed;
        _chatWindow.Activate();
    }

    private void OnMainWindowClosed(object sender, WindowEventArgs args)
    {
        _isAppClosing = true;
        var chatWindow = _chatWindow;
        DetachWindow();
        chatWindow?.Close();
    }

    private void OnChatWindowClosed(object sender, WindowEventArgs args)
        => DetachWindow();

    private void DetachWindow()
    {
        if (_chatWindow is not null)
        {
            _chatWindow.Closed -= OnChatWindowClosed;
            _chatWindow = null;
        }

        if (_mainWindow is not null)
        {
            _mainWindow.Closed -= OnMainWindowClosed;
            _mainWindow = null;
        }
    }
}
