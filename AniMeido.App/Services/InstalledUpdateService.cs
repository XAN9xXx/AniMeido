using System.ComponentModel;
using System.Diagnostics;
using AniMeido.Updates;
using AniMeido.Updates.Install;
using Serilog;

namespace AniMeido.App.Services;

internal enum InstalledUpdateStatus
{
    NoUpdate,
    UpdateAvailable,
    ReinstallRequired,
    PendingReady,
    PreviouslyFailed,
    NetworkError,
    SignatureInvalid,
    InvalidManifest,
}

internal sealed record InstalledUpdateResult(
    InstalledUpdateStatus Status,
    UpdateManifest? Manifest = null,
    string? PendingVersion = null,
    string? FailureReason = null)
{
    public string? LatestVersion => PendingVersion ?? Manifest?.Version.ToString();
}

/// <summary>安装模式更新编排；只有已验签的清单可以进入下载路径，不操作 UI。</summary>
public sealed class InstalledUpdateService
{
    internal const string HttpClientName = "InstalledUpdates";
    private readonly IHttpClientFactory _factory;
    private readonly InstalledAppContext _context;
    private readonly IReadOnlyList<string> _publicKeys;
    private readonly Func<ProcessStartInfo, bool> _startLauncher;
    private readonly SemaphoreSlim _downloadGate = new(1, 1);
    internal string? InstallRoot => _context.InstallRoot;

    public InstalledUpdateService(IHttpClientFactory factory, InstalledAppContext context)
        : this(factory, context, UpdateTrustedKeys.PublicKeys, StartLauncher)
    {
    }

    internal InstalledUpdateService(IHttpClientFactory factory, InstalledAppContext context,
        IReadOnlyList<string> publicKeys, Func<ProcessStartInfo, bool> startLauncher)
    {
        _factory = factory;
        _context = context;
        _publicKeys = publicKeys;
        _startLauncher = startLauncher;
    }

    private InstallManager CreateManager()
    {
        if (!_context.IsInstalled)
            throw new InvalidOperationException("当前不是并列版本目录安装模式。");
        return new InstallManager(new InstallStateStore(_context.InstallRoot!,
            log: message => Log.Warning("安装状态：{Message}", message)));
    }

    internal async Task<InstalledUpdateResult> CheckAsync(string channel,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await _context.StartupCleanup.WaitAsync(cancellationToken).ConfigureAwait(false);
            var manager = CreateManager();
            var state = manager.ReadState();
            if (state.Pending is { } pending)
            {
                if (!InstallLayout.IsValidVersion(_context.InstallRoot!, pending))
                    throw new InstallException("待更新版本目录无效。");
                return new(InstalledUpdateStatus.PendingReady, PendingVersion: pending);
            }
            if (channel is not ("stable" or "preview"))
                throw new UpdateException(UpdateErrorKind.InvalidManifest, "更新通道无效。");
            using var client = _factory.CreateClient(HttpClientName);
            var url = $"https://dl.animeido.com/updates/win-x64/{channel}/release.json";
            using var manifestStream = await client.GetStreamAsync(url, cancellationToken).ConfigureAwait(false);
            using var signatureStream = await client.GetStreamAsync(url + ".sig", cancellationToken).ConfigureAwait(false);
            var manifest = await UpdateManifestReader.ReadAsync(manifestStream, signatureStream,
                _publicKeys, channel, cancellationToken).ConfigureAwait(false);
            var decision = UpdatePolicy.Evaluate(manifest, SemanticVersion.Parse(_context.Version!));
            var status = decision switch
            {
                UpdateDecision.NoUpdate => InstalledUpdateStatus.NoUpdate,
                UpdateDecision.ReinstallRequired => InstalledUpdateStatus.ReinstallRequired,
                _ => state.FailedVersions.Any(version => SemanticVersion.Parse(version) == manifest.Version)
                    ? InstalledUpdateStatus.PreviouslyFailed : InstalledUpdateStatus.UpdateAvailable,
            };
            return new(status, manifest);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            Log.Warning(ex, "安装模式更新检查超时");
            return new(InstalledUpdateStatus.NetworkError, FailureReason: "获取更新清单超时，请重试。");
        }
        catch (HttpRequestException ex)
        {
            Log.Warning(ex, "无法获取安装模式更新清单");
            return new(InstalledUpdateStatus.NetworkError, FailureReason: "无法获取更新清单，请检查网络后重试。");
        }
        catch (UpdateException ex)
        {
            Log.Warning(ex, "安装模式更新清单被拒绝：{Kind}", ex.Kind);
            // 即使解析器将来带有字段详情，失败结果仍不暴露未受信任的清单内容。
            return new(ex.Kind == UpdateErrorKind.InvalidSignature
                ? InstalledUpdateStatus.SignatureInvalid : InstalledUpdateStatus.InvalidManifest,
                FailureReason: ex.Kind == UpdateErrorKind.InvalidSignature
                    ? "更新清单签名无效，不能信任其内容。" : "更新清单无效或与通道不一致。");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Log.Warning(ex, "无法读取安装更新状态");
            return new(InstalledUpdateStatus.InvalidManifest, FailureReason: $"无法读取安装状态：{ex.Message}");
        }
    }

    internal async Task<string> PrepareAsync(UpdateManifest manifest, IProgress<long>? progress = null,
        CancellationToken cancellationToken = default)
    {
        await _downloadGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _context.StartupCleanup.WaitAsync(cancellationToken).ConfigureAwait(false);
            var manager = CreateManager();
            var version = manifest.Version.ToString();
            var target = InstallLayout.VersionDirectory(_context.InstallRoot!, version);
            var state = manager.ReadState();
            if (Directory.Exists(target) || File.Exists(target))
            {
                if (state.Pending == version && InstallLayout.IsValidVersion(_context.InstallRoot!, version))
                {
                    var completed = Path.Combine(_context.InstallRoot!, "packages", manifest.Package.Sha256 + ".zip");
                    if (File.Exists(completed))
                        File.Delete(completed);
                    return version;
                }
                throw new InstallException($"更新目标已存在，未删除或覆盖：{target}");
            }
            if (manifest.Version.CompareTo(SemanticVersion.Parse(_context.Version!)) <= 0)
                throw new InstallException("不能下载并切换到不高于当前安装版本的版本。");
            using var client = _factory.CreateClient(HttpClientName);
            var packages = Path.Combine(_context.InstallRoot!, "packages");
            var zip = await UpdatePackageDownloader.DownloadAsync(manifest.Package, packages, client,
                progress, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            // 解压与 SetPending 是不可取消的收尾阶段；不会覆盖 current 或删除已有目标。
            await Task.Run(() => UpdatePackageExtractor.Extract(zip, target, [InstallLayout.AppFileName]))
                .ConfigureAwait(false);
            manager.SetPending(version);
            File.Delete(zip);
            return version;
        }
        catch (Exception ex) when (ex is UpdateException or IOException or UnauthorizedAccessException
            or OperationCanceledException or InvalidOperationException)
        {
            Log.Warning(ex, "准备应用更新失败，安装根：{Root}", _context.InstallRoot);
            throw;
        }
        finally
        {
            _downloadGate.Release();
        }
    }

    internal void Restart(Action requestExit)
    {
        try
        {
            var manager = CreateManager();
            var pending = manager.ReadState().Pending;
            if (pending is null || !InstallLayout.IsValidVersion(_context.InstallRoot!, pending))
                throw new InstallException("没有有效的待更新版本。");
            var launcher = Path.Combine(_context.InstallRoot!, "AniMeido.exe");
            if (!File.Exists(launcher))
                throw new FileNotFoundException($"找不到启动器：{launcher}", launcher);
            var start = new ProcessStartInfo(launcher)
            {
                UseShellExecute = false,
                WorkingDirectory = _context.InstallRoot!,
            };
            start.ArgumentList.Add("--wait-pid");
            start.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
            if (!_startLauncher(start))
                throw new InstallException("无法创建启动器进程，应用未退出，待更新版本已保留。");
            requestExit();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception
            or InvalidOperationException)
        {
            Log.Warning(ex, "启动更新重启失败，主程序将继续运行");
            throw;
        }
    }

    private static bool StartLauncher(ProcessStartInfo start)
    {
        using var process = Process.Start(start);
        return process is not null;
    }
}
