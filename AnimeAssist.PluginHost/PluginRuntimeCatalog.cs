using AniMeido.Contracts;
using AniMeido.Contracts.Playback;
using AniMeido.PluginProtocol;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Dispatching;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Loader;

namespace AniMeido.PluginHost;

internal sealed class PluginRuntimeCatalog : IAsyncDisposable
{
    private readonly DispatcherQueue _dispatcherQueue;
    private readonly PlaybackProgressEventQueue _playbackProgress;
    private readonly Dictionary<string, HostedPluginDescriptor> _descriptors =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ActivePlugin> _activePlugins =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim _gate = new(1, 1);
    private int _activeInvocationCount;
    private bool _disposed;

    public PluginRuntimeCatalog(
        DispatcherQueue dispatcherQueue,
        PlaybackProgressEventQueue playbackProgress)
    {
        _dispatcherQueue = dispatcherQueue;
        _playbackProgress = playbackProgress;
    }

    public HostedPlaybackProgressEvent[] GetPlaybackProgressEvents()
        => _playbackProgress.GetPendingEvents();

    public void AcknowledgePlaybackProgressEvents(long sequence)
        => _playbackProgress.Acknowledge(sequence);

    public async Task<HostedActivePlaybackContext?>
        GetActivePlaybackContextAsync()
    {
        await _gate.WaitAsync();
        try
        {
            foreach (var active in _activePlugins.Values)
            {
                var provider = active.Services
                    .GetService<IActiveAnimePlaybackContextProvider>();
                if (provider is null)
                {
                    continue;
                }

                var context = await provider.GetActiveContextAsync();
                if (context is not null)
                {
                    return new HostedActivePlaybackContext(
                        context.AnimeId,
                        context.Title,
                        context.EpisodeNumber,
                        context.PositionSeconds,
                        context.ObservedAt);
                }
            }

            return null;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<PluginHostSnapshot> InitializeAsync(
        IReadOnlyList<HostedPluginDescriptor> plugins)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _descriptors.Clear();
        var commands = new List<HostedCommandContribution>();
        var capabilities = new HashSet<string>(StringComparer.Ordinal);
        var failures = new List<HostedPluginFailure>();

        foreach (var plugin in plugins)
        {
            if (!_descriptors.TryAdd(plugin.Manifest.PluginId, plugin))
            {
                failures.Add(new HostedPluginFailure(
                    plugin.Manifest.PluginId,
                    "插件 ID 重复。"));
                continue;
            }

            foreach (var navigation in plugin.Manifest.Contributions.Navigation)
            {
                var command = plugin.Manifest.Contributions.Commands
                    .First(item => string.Equals(
                        item.Id,
                        navigation.Command,
                        StringComparison.Ordinal));
                commands.Add(new HostedCommandContribution(
                    plugin.Manifest.PluginId,
                    command.Id,
                    command.Title,
                    command.Icon));
            }

            foreach (var capability in plugin.Manifest.Contributions.Capabilities)
            {
                capabilities.Add(capability);
            }
        }

        foreach (var plugin in plugins.Where(item =>
            item.Manifest.ActivationEvents.Contains(
                PluginHostProtocol.StartupFinishedActivationEvent,
                StringComparer.Ordinal)))
        {
            try
            {
                await ActivateAsync(plugin.Manifest.PluginId);
            }
            catch (Exception ex) when (
                ex is InvalidOperationException
                or IOException
                or BadImageFormatException
                or ReflectionTypeLoadException)
            {
                failures.Add(new HostedPluginFailure(
                    plugin.Manifest.PluginId,
                    ex.Message));
            }
        }

        return new PluginHostSnapshot(
            commands,
            capabilities.ToList(),
            failures);
    }

    public async Task InvokeCommandAsync(
        string pluginId,
        string commandId)
    {
        Interlocked.Increment(ref _activeInvocationCount);
        try
        {
            var active = await ActivateAsync(pluginId);
            var navigationItem = active.Plugin
                .GetNavigationItems()
                .SingleOrDefault(item =>
                    item.Kind == PluginNavigationItemKind.Command
                    && string.Equals(
                        item.CommandId,
                        commandId,
                        StringComparison.Ordinal))
                ?? throw new InvalidOperationException(
                    $"插件未注册命令：{commandId}");
            await RunOnUiAsync(() =>
            {
                if (navigationItem.Command?.CanExecute(null) == true)
                {
                    navigationItem.Command.Execute(null);
                }

                return Task.CompletedTask;
            });
        }
        finally
        {
            Interlocked.Decrement(ref _activeInvocationCount);
        }
    }

    public async Task LaunchAnimePlaybackAsync(AnimePlaybackRequest request)
    {
        Interlocked.Increment(ref _activeInvocationCount);
        try
        {
            var descriptor = _descriptors.Values.FirstOrDefault(item =>
                item.Manifest.Contributions.Capabilities.Contains(
                    PluginHostProtocol.AnimePlaybackCapability,
                    StringComparer.Ordinal))
                ?? throw new InvalidOperationException("当前没有可用的在线播放插件。");
            var active = await ActivateAsync(descriptor.Manifest.PluginId);
            var launcher = active.Services.GetService<IAnimePlaybackLauncher>()
                ?? throw new InvalidOperationException(
                    "播放插件未注册 IAnimePlaybackLauncher。");
            await RunOnUiAsync(() => launcher.LaunchAsync(
                new AnimePlaybackContext(
                    request.AnimeId,
                    request.Title,
                    request.AlternateTitles)));
        }
        finally
        {
            Interlocked.Decrement(ref _activeInvocationCount);
        }
    }

    public PluginHostRuntimeState GetRuntimeState()
        => new(
            HasVisibleWindows: NativeWindowInspector.HasVisibleWindow(
                Environment.ProcessId),
            ActiveInvocationCount: Volatile.Read(ref _activeInvocationCount));

    private async Task<ActivePlugin> ActivateAsync(string pluginId)
    {
        await _gate.WaitAsync();
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_activePlugins.TryGetValue(pluginId, out var existing))
            {
                return existing;
            }

            if (!_descriptors.TryGetValue(pluginId, out var descriptor))
            {
                throw new InvalidOperationException($"未找到插件：{pluginId}");
            }

            var loadContext = new HostedPluginLoadContext(descriptor.Directory);
            var entryAssemblyPath = Path.GetFullPath(Path.Combine(
                descriptor.Directory,
                descriptor.Manifest.EntryAssembly));
            var assembly = loadContext.LoadFromAssemblyPath(entryAssemblyPath);
            var pluginTypes = assembly.GetExportedTypes()
                .Where(type =>
                    typeof(IPlugin).IsAssignableFrom(type)
                    && type.IsClass
                    && !type.IsAbstract)
                .ToList();
            if (pluginTypes.Count != 1
                || Activator.CreateInstance(pluginTypes[0]) is not IPlugin plugin)
            {
                throw new InvalidOperationException(
                    "插件包必须包含且仅包含一个公开 IPlugin 实现。");
            }

            ValidateIdentity(descriptor.Manifest, plugin);
            var services = new ServiceCollection();
            services.AddSingleton<IAnimePlaybackProgressReporter>(
                _playbackProgress);
            await RunOnUiAsync(() => plugin.InitializeAsync(services));
            var provider = services.BuildServiceProvider();
            var active = new ActivePlugin(plugin, provider, loadContext);
            _activePlugins.Add(pluginId, active);
            return active;
        }
        finally
        {
            _gate.Release();
        }
    }

    private Task RunOnUiAsync(Func<Task> action)
    {
        if (_dispatcherQueue.HasThreadAccess)
        {
            return action();
        }

        var completion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_dispatcherQueue.TryEnqueue(async () =>
        {
            try
            {
                await action();
                completion.SetResult();
            }
#pragma warning disable CA1031 // Preserve arbitrary plugin exceptions across the UI dispatch boundary.
            catch (Exception ex)
            {
                completion.SetException(ex);
            }
#pragma warning restore CA1031
        }))
        {
            completion.SetException(
                new InvalidOperationException("PluginHost UI 线程不可用。"));
        }

        return completion.Task;
    }

    private static void ValidateIdentity(
        PluginManifest manifest,
        IPlugin plugin)
    {
        if (!string.Equals(manifest.PluginId, plugin.PluginID, StringComparison.Ordinal)
            || !string.Equals(manifest.Version, plugin.Version, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("插件清单与运行时身份不一致。");
        }

        if (plugin.IsRequired)
        {
            throw new InvalidOperationException("可选插件不能声明为 required。");
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (var active in _activePlugins.Values)
        {
            await active.Services.DisposeAsync();
        }

        _activePlugins.Clear();
        _gate.Dispose();
    }

    private sealed record ActivePlugin(
        IPlugin Plugin,
        ServiceProvider Services,
        HostedPluginLoadContext LoadContext);
}

internal sealed class HostedPluginLoadContext : AssemblyLoadContext
{
    private static readonly HashSet<string> SharedAssemblyNames =
        new(StringComparer.OrdinalIgnoreCase)
        {
            "AniMeido.Contracts",
            "AniMeido.PluginProtocol",
            "Microsoft.Extensions.DependencyInjection",
            "Microsoft.Extensions.DependencyInjection.Abstractions",
            "Microsoft.Extensions.Logging",
            "Microsoft.Extensions.Logging.Abstractions",
            "Microsoft.Extensions.Options",
            "Microsoft.Extensions.Primitives",
            "Microsoft.Extensions.Http",
        };

    private readonly string _pluginDirectory;

    public HostedPluginLoadContext(string pluginDirectory)
        : base(isCollectible: false)
        => _pluginDirectory = pluginDirectory;

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        if (assemblyName.Name is not null
            && SharedAssemblyNames.Contains(assemblyName.Name))
        {
            return null;
        }

        var path = Path.Combine(
            _pluginDirectory,
            assemblyName.Name + ".dll");
        return File.Exists(path)
            ? LoadFromAssemblyPath(path)
            : null;
    }
}

internal static class NativeWindowInspector
{
    private delegate bool EnumWindowsProc(nint hWnd, nint lParam);

    public static bool HasVisibleWindow(int processId)
    {
        var found = false;
        EnumWindows((window, _) =>
        {
            GetWindowThreadProcessId(window, out var ownerProcessId);
            if (ownerProcessId == processId && IsWindowVisible(window))
            {
                found = true;
                return false;
            }

            return true;
        }, 0);
        return found;
    }

    [DllImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(
        EnumWindowsProc callback,
        nint lParam);

    [DllImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static extern uint GetWindowThreadProcessId(
        nint hWnd,
        out int processId);

    [DllImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(nint hWnd);
}
