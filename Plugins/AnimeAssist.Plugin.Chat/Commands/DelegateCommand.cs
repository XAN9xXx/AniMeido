using System.Windows.Input;

namespace AniMeido.Plugin.Chat.Commands;

internal sealed class DelegateCommand(Action execute) : ICommand
{
    private readonly Action _execute = execute
        ?? throw new ArgumentNullException(nameof(execute));

    public event EventHandler? CanExecuteChanged
    {
        add { }
        remove { }
    }

    public bool CanExecute(object? parameter) => true;

    public void Execute(object? parameter) => _execute();
}
