using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using AniMeido.PluginProtocol;
using System.IO.Pipes;

namespace AniMeido.PluginHost;

public partial class App : Application
{
    private DispatcherQueue? _dispatcherQueue;
    private PluginRuntimeCatalog? _catalog;

    public App()
    {
        InitializeComponent();
        UnhandledException += OnUnhandledException;
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        var pipeName = CommandLineOptions.GetPipeName(
            Environment.GetCommandLineArgs());
        var callbackPipeName = CommandLineOptions.GetCallbackPipeName(
            Environment.GetCommandLineArgs());
        if (string.IsNullOrWhiteSpace(pipeName)
            || string.IsNullOrWhiteSpace(callbackPipeName))
        {
            Exit();
            return;
        }

        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
        _ = ConnectAsync(pipeName, callbackPipeName);
    }

    private async Task ConnectAsync(
        string pipeName,
        string callbackPipeName)
    {
        PluginRuntimeCatalog? catalog = null;
        try
        {
            using var pipe = new NamedPipeClientStream(
                ".",
                pipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            var callbackPipe = new NamedPipeClientStream(
                ".",
                callbackPipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            using var timeout = new CancellationTokenSource(
                TimeSpan.FromSeconds(15));
            await Task.WhenAll(
                pipe.ConnectAsync(timeout.Token),
                callbackPipe.ConnectAsync(timeout.Token));

            using var callbackClient = new JsonPipeRpcClient(callbackPipe);
            catalog = new PluginRuntimeCatalog(
                _dispatcherQueue!,
                new PlaybackProgressEventQueue(),
                new HostedPersonalAnimeDataGateway(callbackClient));
            _catalog = catalog;

            var target = new PluginHostRpcTarget(catalog, callbackPipeName);
            var server = new JsonPipeRpcServer(
                pipe,
                target.DispatchAsync,
                () => target.ShutdownRequested);
            await server.RunAsync();
        }
        catch (Exception ex) when (
            ex is IOException
            or TimeoutException
            or OperationCanceledException)
        {
            HostLog.Write($"IPC connection ended: {ex}");
        }
        finally
        {
            if (catalog is not null)
            {
                await RunOnUiAsync(async () =>
                {
                    try
                    {
                        await catalog.DisposeAsync();
                    }
#pragma warning disable CA1031 // The host must exit even if a plugin cleanup path fails.
                    catch (Exception ex)
                    {
                        HostLog.Write($"Plugin cleanup failed: {ex}");
                    }
#pragma warning restore CA1031
                    finally
                    {
                        Exit();
                    }
                });
            }
            else
            {
                Exit();
            }
        }
    }

    private Task RunOnUiAsync(Func<Task> action)
    {
        if (_dispatcherQueue?.HasThreadAccess == true)
        {
            return action();
        }

        var completion = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        if (_dispatcherQueue?.TryEnqueue(async () =>
        {
            try
            {
                await action();
                completion.SetResult();
            }
#pragma warning disable CA1031 // Host shutdown must propagate any plugin cleanup failure.
            catch (Exception ex)
            {
                completion.SetException(ex);
            }
#pragma warning restore CA1031
        }) != true)
        {
            completion.SetResult();
        }

        return completion.Task;
    }

    private static void OnUnhandledException(
        object sender,
        Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        HostLog.Write($"Unhandled UI exception: {e.Exception}");
        e.Handled = false;
    }
}

internal static class CommandLineOptions
{
    public static string? GetPipeName(IReadOnlyList<string> args)
    {
        for (var index = 0; index < args.Count - 1; index++)
        {
            if (string.Equals(args[index], "--pipe", StringComparison.Ordinal))
            {
                return args[index + 1];
            }
        }

        return null;
    }

    public static string? GetCallbackPipeName(IReadOnlyList<string> args)
    {
        for (var index = 0; index < args.Count - 1; index++)
        {
            if (string.Equals(
                args[index],
                "--callback-pipe",
                StringComparison.Ordinal))
            {
                return args[index + 1];
            }
        }

        return null;
    }
}
