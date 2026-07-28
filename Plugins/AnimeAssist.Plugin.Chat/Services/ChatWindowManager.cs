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

    public void OpenOrActivate()
    {
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

        _chatWindow = new ChatWindow();
        _chatWindow.Closed += OnChatWindowClosed;
        _chatWindow.Activate();
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

    }
}
