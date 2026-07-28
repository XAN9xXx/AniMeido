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
        if (string.IsNullOrWhiteSpace(pipeName))
        {
            Exit();
            return;
        }

        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
        _catalog = new PluginRuntimeCatalog(_dispatcherQueue);
        _ = ConnectAsync(pipeName, _catalog);
    }

    private async Task ConnectAsync(
        string pipeName,
        PluginRuntimeCatalog catalog)
    {
        try
        {
            var pipe = new NamedPipeClientStream(
                ".",
                pipeName,
                PipeDirection.InOut,
                PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
            using var timeout = new CancellationTokenSource(
                TimeSpan.FromSeconds(15));
            await pipe.ConnectAsync(timeout.Token);

            var target = new PluginHostRpcTarget(catalog);
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
}
