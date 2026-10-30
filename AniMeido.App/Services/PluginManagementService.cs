using AniMeido.App.Models;

namespace AniMeido.App.Services;

internal interface IPluginReloadHost
{
    event EventHandler? StatusChanged;
    string StatusText { get; }
    Task<bool> HasActivePluginUiAsync(CancellationToken cancellationToken = default);
    Task ReloadAsync(CancellationToken cancellationToken = default);
}

internal enum PluginChange
{
    Enable,
    Disable,
    Rollback,
    Uninstall,
}

internal sealed record PluginManagementState(
    IReadOnlyList<InstalledPluginInfo> Plugins,
    bool ReloadRequired,
    string HostStatus);

internal sealed record PluginReloadPrompt(bool HasActiveUi, bool RequiresConfirmation);
internal sealed record PluginReloadOutcome(bool Applied, string? FailureMessage = null);

/// <summary>插件管理的业务入口；页面只负责选择、确认和呈现。</summary>
public sealed class PluginManagementService
{
    private readonly PluginPackageManager _packages;
    private readonly IPluginReloadHost _host;

    public PluginManagementService(PluginPackageManager packages, PluginHostSupervisor host)
        : this(packages, (IPluginReloadHost)host)
    {
    }

    internal PluginManagementService(PluginPackageManager packages, IPluginReloadHost host)
    {
        _packages = packages;
        _host = host;
    }

    internal event EventHandler? StatusChanged
    {
        add => _host.StatusChanged += value;
        remove => _host.StatusChanged -= value;
    }

    internal string HostStatus => _host.StatusText;

    internal async Task<PluginManagementState> GetStateAsync()
    {
        var plugins = await _packages.GetInstalledPluginsAsync();
        return new PluginManagementState(plugins, _packages.RestartRequired, _host.StatusText);
    }

    internal Task<PluginInstallResult> InstallAsync(string path)
        => _packages.InstallPackageAsync(path);

    internal Task ApplyChangeAsync(string pluginId, PluginChange change)
        => change switch
        {
            PluginChange.Enable => _packages.SetEnabledAsync(pluginId, true),
            PluginChange.Disable => _packages.SetEnabledAsync(pluginId, false),
            PluginChange.Rollback => _packages.RollbackAsync(pluginId),
            PluginChange.Uninstall => _packages.RequestUninstallAsync(pluginId),
            _ => throw new ArgumentOutOfRangeException(nameof(change)),
        };

    internal async Task<PluginReloadOutcome> ApplyPendingAsync(
        Func<PluginReloadPrompt, Task<bool>> allowReload,
        bool forcePrompt = false)
    {
        try
        {
            var hasActiveUi = await _host.HasActivePluginUiAsync();
            var prompt = new PluginReloadPrompt(hasActiveUi, hasActiveUi || forcePrompt);
            // 无需弹窗时也由调用方确认是否仍可继续，例如设置页已经离开。
            if (!await allowReload(prompt))
                return new PluginReloadOutcome(Applied: false);

            await _host.ReloadAsync();
            return new PluginReloadOutcome(Applied: true);
        }
        catch (Exception ex) when (ex is PluginOperationException or IOException
            or UnauthorizedAccessException or InvalidOperationException or TimeoutException)
        {
            Serilog.Log.Error(ex, "无法重载可选插件");
            return new PluginReloadOutcome(Applied: false, FailureMessage: ex.Message);
        }
    }
}
