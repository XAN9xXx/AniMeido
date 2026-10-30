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
    string? FailureReason = null,
    Exception? Failure = null)
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

    internal Task<InstalledUpdateResult> CheckAsync(string channel,
        CancellationToken cancellationToken = default)
        => Task.Run(() => CheckCoreAsync(channel, cancellationToken), cancellationToken);

    private async Task<InstalledUpdateResult> CheckCoreAsync(string channel,
        CancellationToken cancellationToken)
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
            return new(InstalledUpdateStatus.NetworkError,
                FailureReason: "获取更新清单超时，请重试。", Failure: ex);
        }
        catch (HttpRequestException ex)
        {
            return new(InstalledUpdateStatus.NetworkError,
                FailureReason: "无法获取更新清单，请检查网络后重试。", Failure: ex);
        }
        catch (UpdateException ex)
        {
            // 即使解析器将来带有字段详情，失败结果仍不暴露未受信任的清单内容。
            return new(ex.Kind == UpdateErrorKind.InvalidSignature
                ? InstalledUpdateStatus.SignatureInvalid : InstalledUpdateStatus.InvalidManifest,
                FailureReason: ex.Kind == UpdateErrorKind.InvalidSignature
                    ? "更新清单签名无效，不能信任其内容。" : "更新清单无效或与通道不一致。",
                Failure: ex);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return new(InstalledUpdateStatus.InvalidManifest,
                FailureReason: $"无法读取安装状态：{ex.Message}", Failure: ex);
        }
    }

    internal Task<string> PrepareAsync(UpdateManifest manifest, IProgress<long>? progress = null,
        CancellationToken cancellationToken = default)
        => Task.Run(() => PrepareCoreAsync(manifest, progress, cancellationToken), cancellationToken);

    private async Task<string> PrepareCoreAsync(UpdateManifest manifest, IProgress<long>? progress,
        CancellationToken cancellationToken)
    {
        await _downloadGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _context.StartupCleanup.WaitAsync(cancellationToken).ConfigureAwait(false);
            var manager = CreateManager();
            var version = manifest.Version.ToString();
            cancellationToken.ThrowIfCancellationRequested();
            using var updateLock = manager.AcquireUpdateLock();
            var target = InstallLayout.VersionDirectory(_context.InstallRoot!, version);
            var state = manager.ReadState();
            if (state.PendingRestore is not null)
                throw new InstallException("已有待恢复数据，请先重启完成恢复再更新。");
            if (Directory.Exists(target) || File.Exists(target))
            {
                if (state.Pending == version && InstallLayout.IsValidVersion(_context.InstallRoot!, version))
                {
                    var completed = Path.Combine(_context.InstallRoot!, "packages", manifest.Package.Sha256 + ".zip");
                    DeletePreparedPackage(completed);
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
            UpdatePackageExtractor.Extract(zip, target, [InstallLayout.AppFileName]);
            manager.SetPending(version);
            DeletePreparedPackage(zip);
            return version;
        }
        finally
        {
            _downloadGate.Release();
        }
    }

    private static void DeletePreparedPackage(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Log.Warning(ex, "更新已准备完成；暂时无法清理下载包 {Path}", path);
        }
    }

    internal async Task RestartAsync(Action requestExit)
    {
        await Task.Run(StartRestartLauncher);
        requestExit();
    }

    private void StartRestartLauncher()
    {
        var manager = CreateManager();
        var state = manager.ReadState();
        if (state.PendingRestore is null
            && (state.Pending is not { } pending || !InstallLayout.IsValidVersion(_context.InstallRoot!, pending)))
            throw new InstallException("没有有效的待更新版本或待恢复数据。");
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
            throw new InstallException("无法创建启动器进程，应用未退出，待更新版本或待恢复请求已保留。");
    }

    private static bool StartLauncher(ProcessStartInfo start)
    {
        using var process = Process.Start(start);
        return process is not null;
    }
}
