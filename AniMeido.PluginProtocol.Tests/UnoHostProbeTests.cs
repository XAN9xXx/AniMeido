using System.Diagnostics;
using System.IO.Pipes;
using AniMeido.PluginProtocol;
using Xunit;

namespace AniMeido.PluginProtocol.Tests;

// 单独运行原生验证宿主；普通 CI 不启动桌面窗口。
public sealed class UnoHostProbeFactAttribute : FactAttribute
{
    public UnoHostProbeFactAttribute()
    {
        if (!OperatingSystem.IsWindows() || Environment.GetEnvironmentVariable("ANIMEIDO_UNO_HOST_PROBE") is null)
            Skip = "需要 Windows 和显式指定的 Uno 验证宿主。";
    }
}

public sealed class UnoHostProbeTests
{
    [UnoHostProbeFact]
    public Task EmptyHostSupportsHandshakeAndShutdown() => RunAsync(false);

    [UnoHostProbeFact]
    public Task PluginCanReopenItsLastWindowAndHostExitsOnDisconnect() => RunAsync(true);

    private static async Task RunAsync(bool exerciseWindows)
    {
        var exe = Environment.GetEnvironmentVariable("ANIMEIDO_UNO_HOST_PROBE")!;
        Assert.True(File.Exists(exe), $"找不到验证宿主：{exe}");
        var id = "AniMeido-probe-" + Guid.NewGuid().ToString("N");
        using var command = NewPipe(id);
        using var callback = NewPipe(id + "-callback");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var token = timeout.Token;
        var root = Directory.CreateTempSubdirectory(id);
        var start = new ProcessStartInfo(exe) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe)! };
        start.Environment["ANIMEIDO_DATA_ROOT"] = root.FullName;
        foreach (var arg in new[] { "--pipe", id, "--callback-pipe", id + "-callback" })
            start.ArgumentList.Add(arg);
        using var host = Process.Start(start)!;
        var passed = false;
        try
        {
            await Task.WhenAll(command.WaitForConnectionAsync(token), callback.WaitForConnectionAsync(token));
            using var rpc = new JsonPipeRpcClient(command);
            var handshake = await rpc.InvokeAsync<PluginHostHandshakeResponse>("HandshakeAsync",
                [new PluginHostHandshakeRequest(PluginHostProtocol.Version, "1.0.0", id, id + "-callback")], token);
            Assert.Equal(PluginHostProtocol.Version, handshake.ProtocolVersion);
            await CheckWindow(false);
            HostedPluginDescriptor[] plugins = [];
            if (exerciseWindows)
            {
                var dll = Environment.GetEnvironmentVariable("ANIMEIDO_UNO_PROBE_PLUGIN");
                Assert.True(File.Exists(dll), $"找不到验证插件：{dll}");
                // 包里仅有插件程序集，框架由宿主提供，和实际插件包一致。
                File.Copy(dll!, Path.Combine(root.FullName, Path.GetFileName(dll!)));
                plugins = [new(root.FullName, new PluginManifest
                {
                    PluginId = "Probe", Version = "1.0.0", EntryAssembly = Path.GetFileName(dll!),
                    Contributions = new() { Settings = [new() { Id = "open" }, new() { Id = "close" }] },
                })];
            }
            var snapshot = await rpc.InvokeAsync<PluginHostSnapshot>("InitializeAsync", [plugins], token);
            Assert.Empty(snapshot.Failures);
            Assert.Equal(plugins.Sum(item => item.Manifest.Contributions.Settings.Count), snapshot.Settings.Count);
            if (exerciseWindows)
            {
                foreach (var verb in new[] { "open", "close", "open" })
                {
                    await rpc.InvokeAsync("OpenSettingsAsync", ["Probe", verb], token);
                    await CheckWindow(verb == "open");
                }
                rpc.Dispose(); // 模拟主程序意外断连，插件窗口仍开着。
            }
            else
                await rpc.InvokeAsync("ShutdownAsync", [], token);
            await host.WaitForExitAsync(token);
            Assert.Equal(0, host.ExitCode);
            passed = true;

            async Task CheckWindow(bool expected)
            {
                while ((await rpc.InvokeAsync<PluginHostRuntimeState>("GetRuntimeStateAsync", [], token)).HasVisibleWindows != expected)
                    await Task.Delay(30, token);
            }
        }
        finally
        {
            if (!host.HasExited)
            {
                host.Kill();
                await host.WaitForExitAsync();
            }
            if (passed)
                root.Delete(recursive: true);
            else
                Console.WriteLine($"保留失败现场：{root.FullName}");
        }
    }

    private static NamedPipeServerStream NewPipe(string name) => new(name, PipeDirection.InOut, 1,
        PipeTransmissionMode.Byte, PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
}
