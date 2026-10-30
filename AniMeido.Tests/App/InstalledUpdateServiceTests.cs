using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AniMeido.App.Services;
using AniMeido.Updates;
using AniMeido.Updates.Install;

namespace AniMeido.Tests;

public sealed class InstalledUpdateServiceTests
{
    [Fact]
    public async Task CheckCreatesItsHttpClientOffTheCallingUiContext()
    {
        using var fixture = new Fixture();
        var ui = new PausedUiContext();
        Task<InstalledUpdateResult>? checking = null;
        try
        {
            var previous = SynchronizationContext.Current;
            try
            {
                SynchronizationContext.SetSynchronizationContext(ui);
                checking = fixture.Service.CheckAsync("stable");
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(previous);
            }

            var result = await checking.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(InstalledUpdateStatus.UpdateAvailable, result.Status);
            Assert.Equal("2.3.0", result.LatestVersion);
            Assert.Null(Assert.Single(fixture.ClientContexts));
            Assert.Equal(2, fixture.RequestContexts.Count);
            Assert.All(fixture.RequestContexts, context => Assert.Null(context));
        }
        finally
        {
            ui.Resume();
            if (checking is not null)
                await ObserveSchedulingOperationAsync(checking);
        }
    }

    [Fact]
    public async Task PrepareCreatesItsDownloadClientOffTheCallingUiContext()
    {
        using var fixture = new Fixture();
        var manifest = (await fixture.Service.CheckAsync("stable")).Manifest!;
        fixture.ClientContexts.Clear();
        fixture.RequestContexts.Clear();
        var ui = new PausedUiContext();
        Task<string>? preparing = null;
        try
        {
            var previous = SynchronizationContext.Current;
            try
            {
                SynchronizationContext.SetSynchronizationContext(ui);
                preparing = fixture.Service.PrepareAsync(manifest);
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(previous);
            }

            Assert.Equal("2.3.0", await preparing.WaitAsync(TimeSpan.FromSeconds(5)));
            Assert.Null(Assert.Single(fixture.ClientContexts));
            Assert.Null(Assert.Single(fixture.RequestContexts));
            Assert.Equal("2.2.0", fixture.Store.Read().Current);
            Assert.Equal("2.3.0", fixture.Store.Read().Pending);
        }
        finally
        {
            ui.Resume();
            if (preparing is not null)
                await ObserveSchedulingOperationAsync(preparing);
        }
    }

    private static async Task ObserveSchedulingOperationAsync(Task operation)
    {
        try
        {
            await operation.WaitAsync(TimeSpan.FromSeconds(5));
        }
#pragma warning disable CA1031 // The test owns its result; cleanup still observes the actual operation.
        catch (Exception) when (operation.IsCompleted)
        {
        }
#pragma warning restore CA1031
    }

    [Theory]
    [InlineData("stable", "2.3.0")]
    [InlineData("preview", "2.4.0-preview.1")]
    public async Task CheckUsesChannelAndInstalledDirectoryVersion(string channel, string version)
    {
        using var fixture = new Fixture(channel, version);
        var result = await fixture.Service.CheckAsync(channel);
        Assert.Equal(InstalledUpdateStatus.UpdateAvailable, result.Status);
        Assert.Equal(version, result.LatestVersion);
        Assert.Equal(new[]
        {
            $"https://dl.animeido.com/updates/win-x64/{channel}/release.json",
            $"https://dl.animeido.com/updates/win-x64/{channel}/release.json.sig",
        }, fixture.Requests.Select(request => request.Uri));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SignatureFailureNeverExposesManifest(bool tamper)
    {
        using var fixture = new Fixture();
        if (tamper)
            fixture.ManifestBytes[0] ^= 1;
        else
            fixture.Signature = Encoding.ASCII.GetBytes("invalid");
        var result = await fixture.Service.CheckAsync("stable");
        Assert.Equal(InstalledUpdateStatus.SignatureInvalid, result.Status);
        Assert.Null(result.Manifest);
        Assert.Null(result.LatestVersion);
        Assert.DoesNotContain("2.3.0", result.FailureReason!);
        Assert.DoesNotContain("https://", result.FailureReason!);
    }

    [Fact]
    public async Task PreparedUpdate_RemainsSuccessfulWhenDownloadedPackageCannotBeDeleted()
    {
        using var fixture = new Fixture();
        var manifest = (await fixture.Service.CheckAsync("stable")).Manifest!;
        Assert.Equal("2.3.0", await fixture.Service.PrepareAsync(manifest));
        var package = Path.Combine(fixture.Root, "packages", manifest.Package.Sha256 + ".zip");
        await File.WriteAllBytesAsync(package, fixture.PackageBytes);
        using var held = new FileStream(package, FileMode.Open, FileAccess.Read, FileShare.Read);

        Assert.Equal("2.3.0", await fixture.Service.PrepareAsync(manifest));

        Assert.Equal("2.3.0", fixture.Store.Read().Pending);
        Assert.True(File.Exists(package));
    }

    [Fact]
    public async Task PendingTakesPrecedenceOverChannelAndHttp()
    {
        using var fixture = new Fixture();
        fixture.AddVersion("2.4.0-preview.1");
        fixture.Store.Write(fixture.Store.Read() with { Pending = "2.4.0-preview.1" });
        var result = await fixture.Service.CheckAsync("stable");
        Assert.Equal(InstalledUpdateStatus.PendingReady, result.Status);
        Assert.Equal("2.4.0-preview.1", result.LatestVersion);
        Assert.Empty(fixture.Requests);
    }

    [Fact]
    public async Task CheckWaitsForStartupCleanupBeforeReadingOrRequesting()
    {
        using var fixture = new Fixture();
        var cleanup = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        fixture.SetStartupCleanup(cleanup.Task);
        var check = fixture.Service.CheckAsync("stable");
        try
        {
            Assert.False(check.IsCompleted);
            Assert.Empty(fixture.Requests);
            cleanup.SetResult();
            Assert.Equal(InstalledUpdateStatus.UpdateAvailable, (await check.WaitAsync(TimeSpan.FromSeconds(5))).Status);
        }
        finally
        {
            cleanup.TrySetResult();
            await Task.WhenAny(check).WaitAsync(TimeSpan.FromSeconds(5));
            await ((Task)check).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing | ConfigureAwaitOptions.ContinueOnCapturedContext);
        }
    }

    [Fact]
    public async Task NonInstalledContextNeverUsesSignedUpdateHttp()
    {
        using var fixture = new Fixture();
        var service = fixture.CreateService(_ => false, new InstalledAppContext(null));
        var result = await service.CheckAsync("stable");
        Assert.Equal(InstalledUpdateStatus.InvalidManifest, result.Status);
        Assert.Empty(fixture.Requests);
    }

    [Fact]
    public async Task FailedVersionCanBeOfferedAgain()
    {
        using var fixture = new Fixture();
        fixture.Store.Write(fixture.Store.Read() with { FailedVersions = ["2.3.0+old-build"] });
        Assert.Equal(InstalledUpdateStatus.PreviouslyFailed, (await fixture.Service.CheckAsync("stable")).Status);
    }

    [Theory]
    [InlineData("2.4.0-preview.1", "2.3.0", "2.2.0", 0)]
    [InlineData("2.2.0", "2.3.0", "2.2.1", 2)]
    public async Task NoDowngradeAndMinimumInstallVersion(string current, string release, string minimum, int expected)
    {
        using var fixture = new Fixture(currentVersion: current, version: release, minimum: minimum);
        Assert.Equal((InstalledUpdateStatus)expected, (await fixture.Service.CheckAsync("stable")).Status);
    }

    [Fact]
    public async Task NetworkErrorAndSignedWrongChannelDoNotExposeManifest()
    {
        using var fixture = new Fixture();
        fixture.Response = _ => throw new HttpRequestException("fake offline failure");
        var failed = await fixture.Service.CheckAsync("stable");
        Assert.Equal(InstalledUpdateStatus.NetworkError, failed.Status);
        Assert.Null(failed.Manifest);
        fixture.Response = null;
        var wrongChannel = await fixture.Service.CheckAsync("preview");
        Assert.Equal(InstalledUpdateStatus.InvalidManifest, wrongChannel.Status);
        Assert.Null(wrongChannel.Manifest);
    }

    [Fact]
    public async Task PrepareSetsPendingAndDeletesHashZipWithoutChangingCurrent()
    {
        using var fixture = new Fixture();
        var manifest = (await fixture.Service.CheckAsync("stable")).Manifest!;
        Assert.Equal("2.3.0", await fixture.Service.PrepareAsync(manifest));
        var state = fixture.Store.Read();
        Assert.Equal("2.2.0", state.Current);
        Assert.Equal("2.3.0", state.Pending);
        Assert.True(File.Exists(InstallLayout.ExecutablePath(fixture.Root, "2.3.0")));
        Assert.Empty(Directory.GetFiles(Path.Combine(fixture.Root, "packages")));
    }

    [Fact]
    public async Task CancelLeavesPartialAndRetryUsesRange()
    {
        using var fixture = new Fixture();
        var manifest = (await fixture.Service.CheckAsync("stable")).Manifest!;
        using var cancellation = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Service.PrepareAsync(manifest,
            new InlineProgress(bytes =>
            {
                if (bytes > 0 && bytes < manifest.Package.Size)
                    cancellation.Cancel();
            }), cancellation.Token));
        var partial = Path.Combine(fixture.Root, "packages", manifest.Package.Sha256 + ".zip.partial");
        Assert.True(File.Exists(partial));
        var offset = new FileInfo(partial).Length;
        Assert.InRange(offset, 1, manifest.Package.Size - 1);
        Assert.Null(fixture.Store.Read().Pending);
        await fixture.Service.PrepareAsync(manifest);
        Assert.Contains(fixture.Requests, request => request.From == offset);
        Assert.False(File.Exists(partial));
    }

    [Fact]
    public async Task WrongHashNeverExtractsOrSetsPending()
    {
        using var fixture = new Fixture();
        var manifest = (await fixture.Service.CheckAsync("stable")).Manifest!;
        fixture.PackageBytes[10] ^= 1;
        var error = await Assert.ThrowsAsync<UpdateException>(() => fixture.Service.PrepareAsync(manifest));
        Assert.Equal(UpdateErrorKind.HashMismatch, error.Kind);
        Assert.Null(fixture.Store.Read().Pending);
        Assert.False(Directory.Exists(InstallLayout.VersionDirectory(fixture.Root, "2.3.0")));
        Assert.Empty(Directory.GetFiles(Path.Combine(fixture.Root, "packages")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExistingTargetOnlyReusableWhenAlreadyPending(bool pending)
    {
        using var fixture = new Fixture();
        var manifest = (await fixture.Service.CheckAsync("stable")).Manifest!;
        var target = fixture.AddVersion("2.3.0");
        var marker = Path.Combine(target, "untouched.txt");
        File.WriteAllText(marker, "keep");
        var requestCount = fixture.Requests.Count;
        if (pending)
        {
            fixture.Store.Write(fixture.Store.Read() with { Pending = "2.3.0" });
            Assert.Equal("2.3.0", await fixture.Service.PrepareAsync(manifest));
        }
        else
            await Assert.ThrowsAsync<InstallException>(() => fixture.Service.PrepareAsync(manifest));
        Assert.Equal("keep", File.ReadAllText(marker));
        Assert.Equal(requestCount, fixture.Requests.Count);
    }

    [Fact]
    public async Task SetPendingFailureLeavesExtractedDirectoryAndCurrentIntact()
    {
        using var fixture = new Fixture(lockTimeout: TimeSpan.Zero);
        var manifest = (await fixture.Service.CheckAsync("stable")).Manifest!;
        FileStream? held = null;
        try
        {
            await Assert.ThrowsAsync<InstallException>(() => fixture.Service.PrepareAsync(manifest,
                new InlineProgress(bytes =>
                {
                    if (bytes == manifest.Package.Size && held is null)
                        held = new FileStream(Path.Combine(fixture.Root, "state.lock"), FileMode.Open,
                            FileAccess.ReadWrite, FileShare.None);
                })));
        }
        finally
        {
            held?.Dispose();
        }
        Assert.Equal("2.2.0", fixture.Store.Read().Current);
        Assert.Null(fixture.Store.Read().Pending);
        Assert.True(Directory.Exists(InstallLayout.VersionDirectory(fixture.Root, "2.3.0")));
    }

    [Fact]
    public async Task ConcurrentPrepareOnlyDownloadsOnce()
    {
        using var fixture = new Fixture();
        var manifest = (await fixture.Service.CheckAsync("stable")).Manifest!;
        await Task.WhenAll(fixture.Service.PrepareAsync(manifest), fixture.Service.PrepareAsync(manifest));
        Assert.Single(fixture.Requests, request => request.Uri.EndsWith(".zip", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PrepareCoordinatesWithCleanupInAnotherInstance()
    {
        using var fixture = new Fixture();
        var manifest = (await fixture.Service.CheckAsync("stable")).Manifest!;
        var other = new InstallManager(new InstallStateStore(fixture.Root));
        var checkedLock = false;

        await fixture.Service.PrepareAsync(manifest, new InlineProgress(_ =>
        {
            Assert.Single(other.CleanupObsolete());
            Assert.Throws<InstallException>(() => other.AcquireUpdateLock());
            checkedLock = true;
        }));

        Assert.True(checkedLock);
        Assert.Empty(other.CleanupObsolete());
        Assert.Equal("2.3.0", other.ReadState().Pending);
        Assert.True(InstallLayout.IsValidVersion(fixture.Root, "2.3.0"));
    }

    [Fact]
    public async Task PrepareRejectsOtherInstanceUpdateBeforeDownloading()
    {
        using var fixture = new Fixture();
        var manifest = (await fixture.Service.CheckAsync("stable")).Manifest!;
        var other = new InstallManager(new InstallStateStore(fixture.Root));
        using (other.AcquireUpdateLock())
        {
            await Assert.ThrowsAsync<InstallException>(() => fixture.Service.PrepareAsync(manifest));
            Assert.DoesNotContain(fixture.Requests, request => request.Uri.EndsWith(".zip", StringComparison.Ordinal));
            Assert.Null(other.ReadState().Pending);
        }
        Assert.Equal("2.3.0", await fixture.Service.PrepareAsync(manifest));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MissingOrUncreatableLauncherNeverRequestsExit(bool exists)
    {
        using var fixture = new Fixture();
        fixture.AddVersion("2.3.0");
        fixture.Store.Write(fixture.Store.Read() with { Pending = "2.3.0" });
        if (exists)
            File.WriteAllText(Path.Combine(fixture.Root, "AniMeido.exe"), "never executed");
        var exit = false;
        await Assert.ThrowsAnyAsync<IOException>(() => fixture.Service.RestartAsync(() => exit = true));
        Assert.False(exit);
        Assert.Equal("2.3.0", fixture.Store.Read().Pending);
    }

    [Fact]
    public async Task RestartCreatesLauncherWithWaitPidBeforeExit()
    {
        using var fixture = new Fixture();
        fixture.AddVersion("2.3.0");
        fixture.Store.Write(fixture.Store.Read() with { Pending = "2.3.0" });
        File.WriteAllText(Path.Combine(fixture.Root, "AniMeido.exe"), "never executed");
        var started = false;
        var service = fixture.CreateService(start =>
        {
            Assert.Equal(Path.Combine(fixture.Root, "AniMeido.exe"), start.FileName);
            Assert.Equal(fixture.Root, start.WorkingDirectory);
            Assert.False(start.UseShellExecute);
            Assert.Equal(new[] { "--wait-pid", Environment.ProcessId.ToString() }, start.ArgumentList);
            started = true;
            return true;
        });
        var exited = false;
        await service.RestartAsync(() => { Assert.True(started); exited = true; });
        Assert.True(exited);
    }

    [Fact]
    public async Task RestartStartsLauncherOnWorkerThenRequestsExitOnTheCallingUiContext()
    {
        using var fixture = new Fixture();
        fixture.AddVersion("2.3.0");
        fixture.Store.Write(fixture.Store.Read() with { Pending = "2.3.0" });
        File.WriteAllText(Path.Combine(fixture.Root, "AniMeido.exe"), "never executed");
        using var ui = new QueuedRestartContext();
        using var releaseLauncher = new ManualResetEventSlim(false);
        var launcherEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        SynchronizationContext? launcherContext = ui;
        var launcherSucceeded = false;
        var exitRequested = false;
        var service = fixture.CreateService(_ =>
        {
            launcherContext = SynchronizationContext.Current;
            launcherEntered.TrySetResult();
            if (!releaseLauncher.Wait(TimeSpan.FromSeconds(5)))
                throw new TimeoutException("The test did not release the launcher.");
            launcherSucceeded = true;
            return true;
        });
        Task? restarting = null;
        try
        {
            var previous = SynchronizationContext.Current;
            try
            {
                SynchronizationContext.SetSynchronizationContext(ui);
                restarting = service.RestartAsync(() =>
                {
                    Assert.True(launcherSucceeded);
                    Assert.Same(ui, SynchronizationContext.Current);
                    exitRequested = true;
                });
            }
            finally
            {
                SynchronizationContext.SetSynchronizationContext(previous);
            }

            await launcherEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Null(launcherContext);
            Assert.False(exitRequested);
            releaseLauncher.Set();
            await ui.Posted.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(exitRequested);

            ui.RunPending();
            await restarting.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.True(exitRequested);
            Assert.Equal("2.3.0", fixture.Store.Read().Pending);
        }
        finally
        {
            releaseLauncher.Set();
            if (restarting is not null)
            {
                try
                {
                    await ui.DrainAsync(restarting);
                }
                finally
                {
                    await ObserveSchedulingOperationAsync(restarting);
                }
            }
        }
    }

    [Fact]
    public async Task LauncherProcessCreationExceptionPreservesPendingAndDoesNotExit()
    {
        using var fixture = new Fixture();
        fixture.AddVersion("2.3.0");
        fixture.Store.Write(fixture.Store.Read() with { Pending = "2.3.0" });
        File.WriteAllText(Path.Combine(fixture.Root, "AniMeido.exe"), "never executed");
        var service = fixture.CreateService(_ => throw new System.ComponentModel.Win32Exception("fake process failure"));
        var exit = false;
        await Assert.ThrowsAsync<System.ComponentModel.Win32Exception>(() => service.RestartAsync(() => exit = true));
        Assert.False(exit);
        Assert.Equal("2.3.0", fixture.Store.Read().Pending);
    }

    private sealed class InlineProgress(Action<long> report) : IProgress<long>
    {
        public void Report(long value) => report(value);
    }

    [Fact]
    public async Task RestoreRequestUsesExistingWaitPidRestartAndLauncherFailureNeverExits()
    {
        using var fixture = new Fixture();
        var request = new PendingRestoreRequest
        {
            Directory = Path.Combine(fixture.Root, "synthetic-restore"), RequestedAt = DateTimeOffset.UtcNow,
        };
        fixture.Store.Write(fixture.Store.Read() with { PendingRestore = request });
        File.WriteAllText(Path.Combine(fixture.Root, "AniMeido.exe"), "never executed");
        var exit = false;
        await Assert.ThrowsAsync<InstallException>(() => fixture.Service.RestartAsync(() => exit = true));
        Assert.False(exit);
        Assert.Equal(request, fixture.Store.Read().PendingRestore);
        var startCalled = false;
        await fixture.CreateService(start =>
        {
            Assert.Equal(new[] { "--wait-pid", Environment.ProcessId.ToString() }, start.ArgumentList);
            Assert.False(start.UseShellExecute);
            startCalled = true;
            return true;
        }).RestartAsync(() => { Assert.True(startCalled); exit = true; });
        Assert.True(exit);
        Assert.Equal(request, fixture.Store.Read().PendingRestore);
        Assert.Null(fixture.Store.Read().Pending);
    }

    [Fact]
    public async Task RestoreRequestPreventsUpdateDownloadAndExtraction()
    {
        using var fixture = new Fixture();
        var manifest = (await fixture.Service.CheckAsync("stable")).Manifest!;
        fixture.Store.Write(fixture.Store.Read() with
        {
            PendingRestore = new PendingRestoreRequest
            {
                Directory = Path.Combine(fixture.Root, "synthetic-restore"), RequestedAt = DateTimeOffset.UtcNow,
            },
        });
        await Assert.ThrowsAsync<InstallException>(() => fixture.Service.PrepareAsync(manifest));
        Assert.DoesNotContain(fixture.Requests, request => request.Uri.EndsWith(".zip", StringComparison.Ordinal));
        Assert.False(Directory.Exists(InstallLayout.VersionDirectory(fixture.Root, "2.3.0")));
        Assert.Null(fixture.Store.Read().Pending);
        Assert.NotNull(fixture.Store.Read().PendingRestore);
    }

    private sealed class QueuedRestartContext : SynchronizationContext, IDisposable
    {
        private readonly ConcurrentQueue<(SendOrPostCallback Callback, object? State)> _callbacks = new();
        private readonly TaskCompletionSource _posted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly SemaphoreSlim _pending = new(0);

        public Task Posted => _posted.Task;

        public override void Post(SendOrPostCallback callback, object? state)
        {
            _callbacks.Enqueue((callback, state));
            _pending.Release();
            _posted.TrySetResult();
        }

        public void RunPending()
        {
            var previous = Current;
            try
            {
                SetSynchronizationContext(this);
                while (_callbacks.TryDequeue(out var work))
                    work.Callback(work.State);
            }
            finally
            {
                SetSynchronizationContext(previous);
            }
        }

        public async Task DrainAsync(Task operation)
        {
            while (!operation.IsCompleted)
            {
                if (!await _pending.WaitAsync(TimeSpan.FromSeconds(5)))
                    throw new TimeoutException("The restart continuation did not arrive.");
                RunPending();
            }
        }

        public void Dispose() => _pending.Dispose();
    }

    internal sealed class Fixture : IDisposable
    {
        private readonly MockAppDataPaths _paths = new();
        private readonly ECDsa _key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        private readonly InstalledAppContext _context;
        public string Root { get; }
        public InstallStateStore Store { get; }
        public InstalledUpdateService Service { get; }
        internal InstalledAppContext Context => _context;
        public byte[] ManifestBytes { get; }
        public byte[] Signature { get; set; }
        public byte[] PackageBytes { get; }
        public List<(string Uri, long? From)> Requests { get; } = [];
        public List<SynchronizationContext?> ClientContexts { get; } = [];
        public List<SynchronizationContext?> RequestContexts { get; } = [];
        public Func<HttpRequestMessage, HttpResponseMessage>? Response { get; set; }

        public Fixture(string channel = "stable", string version = "2.3.0",
            string currentVersion = "2.2.0", string minimum = "2.2.0", TimeSpan? lockTimeout = null)
        {
            Root = Path.Combine(_paths.RootDirectory, "install");
            AddVersion(currentVersion);
            Store = new InstallStateStore(Root);
            Store.Write(new InstallState { Current = currentVersion, CurrentHealthy = true });
            _context = new InstalledAppContext(InstallLayout.ExecutablePath(Root, currentVersion), lockTimeout);
            using var zip = new MemoryStream();
            using (var archive = new ZipArchive(zip, ZipArchiveMode.Create, leaveOpen: true))
            {
                using var output = archive.CreateEntry(InstallLayout.AppFileName, CompressionLevel.NoCompression).Open();
                output.Write(new byte[200_000]); // 大于下载器缓冲区，保证取消测试留下一部分。
            }
            PackageBytes = zip.ToArray();
            ManifestBytes = JsonSerializer.SerializeToUtf8Bytes(new
            {
                schema = 1, channel, version, releaseDate = "2026-10-04", minUpdatableFrom = minimum, notes = "test notes",
                package = new
                {
                    urls = new[] { $"https://dl.animeido.com/updates/win-x64/{channel}/test.zip" },
                    size = PackageBytes.Length, sha256 = Convert.ToHexString(SHA256.HashData(PackageBytes)),
                },
            });
            Signature = Encoding.ASCII.GetBytes(Convert.ToBase64String(_key.SignData(ManifestBytes,
                HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation)));
            Service = CreateService(_ => false);
        }

        public string AddVersion(string version)
        {
            var directory = InstallLayout.VersionDirectory(Root, version);
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, InstallLayout.AppFileName), "never executed");
            return directory;
        }

        public void SetStartupCleanup(Task task) => _context.StartupCleanup = task;

        public InstalledUpdateService CreateService(Func<ProcessStartInfo, bool> start, InstalledAppContext? context = null)
            => new(new Factory(this), context ?? _context, [Convert.ToBase64String(_key.ExportSubjectPublicKeyInfo())], start);

        private HttpResponseMessage Respond(HttpRequestMessage request)
        {
            RequestContexts.Add(SynchronizationContext.Current);
            var uri = request.RequestUri!.AbsoluteUri;
            var offset = request.Headers.Range?.Ranges.First().From;
            Requests.Add((uri, offset));
            if (Response is not null)
                return Response(request);
            var bytes = uri.EndsWith(".sig", StringComparison.Ordinal) ? Signature
                : uri.EndsWith(".json", StringComparison.Ordinal) ? ManifestBytes : PackageBytes;
            var content = new ByteArrayContent(bytes, (int)(offset ?? 0), bytes.Length - (int)(offset ?? 0));
            if (offset is { } from)
                content.Headers.ContentRange = new ContentRangeHeaderValue(from, bytes.Length - 1, bytes.Length);
            return new HttpResponseMessage(offset is null ? HttpStatusCode.OK : HttpStatusCode.PartialContent)
                { RequestMessage = request, Content = content };
        }

        private sealed class Factory(Fixture fixture) : IHttpClientFactory
        {
            public HttpClient CreateClient(string name)
            {
                Assert.Equal(InstalledUpdateService.HttpClientName, name);
                fixture.ClientContexts.Add(SynchronizationContext.Current);
                return new HttpClient(new Handler(fixture));
            }
        }

        private sealed class Handler(Fixture fixture) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
            {
                token.ThrowIfCancellationRequested();
                return Task.FromResult(fixture.Respond(request));
            }
        }

        public void Dispose()
        {
            _key.Dispose();
            _paths.Dispose();
        }
    }
}
