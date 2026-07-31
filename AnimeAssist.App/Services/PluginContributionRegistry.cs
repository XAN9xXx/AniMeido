using AniMeido.Contracts;
using AniMeido.PluginProtocol;
using System.Windows.Input;

namespace AniMeido.App.Services;

public sealed class PluginContributionRegistry
{
    private IReadOnlyList<PluginNavigationItem> _builtInItems = [];
    private IReadOnlyList<HostedCommandContribution> _hostedCommands = [];
    private IReadOnlyList<HostedSettingsContribution> _hostedSettings = [];

    public event EventHandler? Changed;

    public IReadOnlyList<PluginNavigationItem> NavigationItems
        => _builtInItems
            .Concat(_hostedCommands.Select(command =>
                PluginNavigationItem.CreateCommand(
                    command.Title,
                    command.Icon,
                    command.CommandId,
                    new HostedPluginCommand(
                        () => InvokeHostedCommandAsync(
                            command.PluginId,
                            command.CommandId)))))
            .ToList();

    public IReadOnlyList<HostedSettingsContribution> Settings
        => _hostedSettings;

    internal Func<string, string, Task>? CommandInvoker { get; set; }
    internal Func<string, string, Task>? SettingsInvoker { get; set; }

    public void SetBuiltInItems(
        IReadOnlyList<PluginNavigationItem> builtInItems)
    {
        _builtInItems = builtInItems;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void SetHostedContributions(
        IReadOnlyList<HostedCommandContribution> hostedCommands,
        IReadOnlyList<HostedSettingsContribution> hostedSettings)
    {
        _hostedCommands = hostedCommands;
        _hostedSettings = hostedSettings;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public Task OpenSettingsAsync(
        string pluginId,
        string settingsId)
        => SettingsInvoker?.Invoke(pluginId, settingsId)
            ?? Task.FromException(
                new InvalidOperationException("PluginHost 尚未连接。"));

    private Task InvokeHostedCommandAsync(
        string pluginId,
        string commandId)
        => CommandInvoker?.Invoke(pluginId, commandId)
            ?? Task.FromException(
                new InvalidOperationException("PluginHost 尚未连接。"));

    private sealed class HostedPluginCommand : ICommand
    {
        private readonly Func<Task> _execute;

        public HostedPluginCommand(Func<Task> execute)
            => _execute = execute;

        public event EventHandler? CanExecuteChanged
        {
            add { }
            remove { }
        }

        public bool CanExecute(object? parameter) => true;

        public async void Execute(object? parameter)
        {
            try
            {
                await _execute();
            }
#pragma warning disable CA1031 // Command failures are surfaced by PluginHost status.
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[PluginHost] Command failed: {ex}");
            }
#pragma warning restore CA1031
        }
    }
}
