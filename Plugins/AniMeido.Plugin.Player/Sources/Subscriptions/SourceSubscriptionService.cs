using AniMeido.Plugin.Player.Sources.Packages;
using AniMeido.Plugin.Player.Sources.EasyBangumi;
using AniMeido.Plugin.Player.Sources.Animeko;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AniMeido.Plugin.Player.Sources.Subscriptions;

internal sealed partial class SourceSubscriptionService
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };
    private readonly GitHubSourceReader _reader;
    private readonly SourcePackageInstaller _installer;
    private readonly string _statePath;
    private readonly ILogger<SourceSubscriptionService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public SourceSubscriptionService(
        HttpClient httpClient,
        SourcePackageInstaller installer,
        ILogger<SourceSubscriptionService>? logger = null)
        : this(
            new GitHubSourceReader(httpClient),
            installer,
            Path.Combine(
                SourcePackageInstaller.GetSourcesDirectory(),
                "Subscriptions",
                "subscriptions.json"), logger)
    {
    }

    internal SourceSubscriptionService(
        GitHubSourceReader reader,
        SourcePackageInstaller installer,
        string statePath,
        ILogger<SourceSubscriptionService>? logger = null)
    {
        _reader = reader;
        _installer = installer;
        _statePath = statePath;
        _logger = logger ?? NullLogger<SourceSubscriptionService>.Instance;
    }

    public async Task<IReadOnlyList<SourceSubscriptionState>> ListAsync(
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return (await LoadAsync(cancellationToken)).Subscriptions
                .OrderBy(item => item.Kind)
                .ThenBy(item => item.Url, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<SourceSubscriptionPreview> PreviewAsync(
        string url,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(url);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var document = await LoadAsync(cancellationToken);
            var normalizedUrl = NormalizeUrl(url);
            var subscriptionId = CreateStableId("subscription", normalizedUrl);
            var existing = document.Subscriptions.FirstOrDefault(item =>
                string.Equals(item.Id, subscriptionId, StringComparison.Ordinal));
            var kind = DetectKind(normalizedUrl);
            if (existing is not null && existing.Kind != kind)
            {
                throw new InvalidDataException("订阅 URL 类型与现有记录不一致。");
            }

            var existingByPath = (existing?.Sources ?? [])
                .ToDictionary(
                    item => item.UpstreamPath,
                    StringComparer.Ordinal);
            var knownVersions = existingByPath.Values
                .Where(item => !string.IsNullOrWhiteSpace(item.UpstreamBlobId))
                .ToDictionary(item => item.UpstreamPath, item => item.UpstreamBlobId!, StringComparer.Ordinal);
            var remoteFiles = await _reader.ReadAsync(
                normalizedUrl, kind, knownVersions, cancellationToken);
            var previewItems = new List<SubscriptionPreviewItem>();
            var seenPaths = new HashSet<string>(StringComparer.Ordinal);
            foreach (var file in remoteFiles)
            {
                seenPaths.Add(file.Path);
                existingByPath.TryGetValue(file.Path, out var previous);
                previewItems.Add(CreatePreviewItem(
                    subscriptionId,
                    normalizedUrl,
                    kind,
                    file,
                    previous));
            }

            foreach (var previous in existingByPath.Values.Where(item =>
                !seenPaths.Contains(item.UpstreamPath)))
            {
                previewItems.Add(new SubscriptionPreviewItem(
                    previous.UpstreamPath,
                    previous.SourceId,
                    previous.DisplayName,
                    previous.Revision,
                    previous.RevisionNumber,
                    SubscriptionChangeKind.Orphaned,
                    "上游已移除，本地源将保留并禁用。"));
            }

            cancellationToken.ThrowIfCancellationRequested();
            await SavePreviewCacheAsync(document, existingByPath, previewItems, cancellationToken);
            return new SourceSubscriptionPreview(
                subscriptionId,
                normalizedUrl,
                kind,
                previewItems
                    .OrderBy(item => item.Change)
                    .ThenBy(item => item.DisplayName, StringComparer.CurrentCulture)
                    .ToArray());
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<SubscriptionApplyResult> ApplyAsync(
        SourceSubscriptionPreview preview,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(preview);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var document = await LoadAsync(cancellationToken);
            var subscription = document.Subscriptions.FirstOrDefault(item =>
                string.Equals(
                    item.Id,
                    preview.SubscriptionId,
                    StringComparison.Ordinal));
            if (subscription is null)
            {
                subscription = new SourceSubscriptionState
                {
                    Id = preview.SubscriptionId,
                    Url = preview.Url,
                    Kind = preview.Kind,
                };
                document.Subscriptions.Add(subscription);
            }

            subscription.Url = preview.Url;
            subscription.Kind = preview.Kind;
            // 首次安装前先持久化订阅身份；即使进程在安装后退出，也能从包清单恢复逐项状态。
            await SaveAsync(document, cancellationToken);
            var results = new List<SubscriptionItemResult>();
            foreach (var item in preview.Items.Where(item => item.Change is
                SubscriptionChangeKind.Added or SubscriptionChangeKind.Updated or SubscriptionChangeKind.Orphaned))
            {
                if (cancellationToken.IsCancellationRequested)
                    return new SubscriptionApplyResult(results, Cancelled: true);

                try
                {
                    await ApplyItemAsync(preview, item, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    return new SubscriptionApplyResult(results, Cancelled: true);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
                {
                    _logger.LogError(ex, "Subscription source {Path} could not be applied", item.UpstreamPath);
                    results.Add(new SubscriptionItemResult(item.UpstreamPath, item.DisplayName, false, ex.Message));
                    continue;
                }

                subscription.Sources.RemoveAll(source => source.UpstreamPath == item.UpstreamPath);
                subscription.Sources.Add(new SubscriptionSourceState
                {
                    UpstreamPath = item.UpstreamPath,
                    SourceId = item.SourceId,
                    DisplayName = item.DisplayName,
                    Revision = item.Revision,
                    UpstreamBlobId = item.UpstreamBlobId,
                    RevisionNumber = item.RevisionNumber,
                    IsOrphaned = item.Change == SubscriptionChangeKind.Orphaned,
                });
                subscription.LastRefreshUtc = DateTimeOffset.UtcNow;
                results.Add(new SubscriptionItemResult(item.UpstreamPath, item.DisplayName, true));
                try
                {
                    // 包已提交，调用方取消不能跳过对应记录。
                    await SaveAsync(document, CancellationToken.None);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    _logger.LogWarning(ex, "Sources committed; subscription state will be reconciled on next load");
                    return new SubscriptionApplyResult(results, Warning:
                        $"已安装的源保留，但订阅记录暂未保存。下次刷新会核对已安装的源。{ex.Message}");
                }
            }
            return new SubscriptionApplyResult(results);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task ApplyItemAsync(
        SourceSubscriptionPreview preview, SubscriptionPreviewItem item, CancellationToken cancellationToken)
    {
        if (item.Change == SubscriptionChangeKind.Orphaned)
        {
            await _installer.MarkOrphanedAsync(item.SourceId, cancellationToken);
            return;
        }

        await _installer.InstallSubscriptionSourceAsync(CreateManifest(preview, item),
            item.Content ?? throw new InvalidDataException($"预览内容已失效：{item.UpstreamPath}"),
            cancellationToken);
    }

    public async Task RemoveAsync(
        string subscriptionId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(subscriptionId);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var document = await LoadAsync(cancellationToken);
            var subscription = document.Subscriptions.FirstOrDefault(item =>
                string.Equals(item.Id, subscriptionId, StringComparison.Ordinal));
            if (subscription is null)
            {
                return;
            }

            foreach (var source in subscription.Sources)
            {
                try
                {
                    await _installer.MarkUnmanagedAsync(
                        source.SourceId,
                        cancellationToken);
                }
                catch (DirectoryNotFoundException)
                {
                }
            }

            document.Subscriptions.Remove(subscription);
            await SaveAsync(document, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static SubscriptionPreviewItem CreatePreviewItem(
        string subscriptionId,
        string normalizedUrl,
        SourceSubscriptionKind kind,
        GitHubSourceFile file,
        SubscriptionSourceState? previous)
    {
        if (file.Content is null && previous is not null)
        {
            return new SubscriptionPreviewItem(file.Path, previous.SourceId, previous.DisplayName,
                previous.Revision, previous.RevisionNumber, SubscriptionChangeKind.Unchanged)
            {
                UpstreamBlobId = file.BlobId,
            };
        }

        var content = file.Content ?? throw new InvalidDataException($"源文件缺少正文：{file.Path}");
        var revision = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(content)))
            .ToLowerInvariant();
        if (kind == SourceSubscriptionKind.EasyBangumi
            && Path.GetFileName(file.Path).StartsWith(
                "block-",
                StringComparison.OrdinalIgnoreCase))
        {
            return new SubscriptionPreviewItem(
                file.Path,
                previous?.SourceId ?? string.Empty,
                Path.GetFileNameWithoutExtension(file.Path),
                revision,
                previous?.RevisionNumber ?? 0,
                SubscriptionChangeKind.Skipped,
                "文件名以 block- 开头。");
        }

        try
        {
            var metadata = kind == SourceSubscriptionKind.EasyBangumi
                ? ReadEasyMetadata(content)
                : ReadAnimekoMetadata(content);
            var sourceId = kind == SourceSubscriptionKind.EasyBangumi
                ? SanitizeId($"easybangumi.{metadata.Key}")
                : CreateStableId(
                    "animeko.web",
                    $"{normalizedUrl}\n{file.Path}");
            var change = previous is null
                ? SubscriptionChangeKind.Added
                : string.Equals(
                    previous.Revision,
                    revision,
                    StringComparison.Ordinal)
                    ? SubscriptionChangeKind.Unchanged
                    : SubscriptionChangeKind.Updated;
            return new SubscriptionPreviewItem(
                file.Path,
                sourceId,
                metadata.DisplayName,
                revision,
                change == SubscriptionChangeKind.Updated
                    ? previous!.RevisionNumber + 1
                    : previous?.RevisionNumber ?? 1,
                change)
            {
                Content = change == SubscriptionChangeKind.Unchanged ? null : content,
                UpstreamBlobId = file.BlobId,
            };
        }
#pragma warning disable CA1031 // Invalid siblings must not block a subscription preview.
        catch (Exception ex)
        {
            return new SubscriptionPreviewItem(
                file.Path,
                previous?.SourceId ?? string.Empty,
                previous?.DisplayName
                    ?? Path.GetFileNameWithoutExtension(file.Path),
                previous?.Revision ?? revision,
                previous?.RevisionNumber ?? 0,
                SubscriptionChangeKind.Invalid,
                ex.Message);
        }
#pragma warning restore CA1031
    }

    private static SourcePackageManifest CreateManifest(
        SourceSubscriptionPreview preview,
        SubscriptionPreviewItem item)
        => new()
        {
            FormatVersion = 2,
            Id = item.SourceId,
            DisplayName = item.DisplayName,
            Version = $"0.0.{Math.Max(1, item.RevisionNumber)}",
            EntryFile = preview.Kind == SourceSubscriptionKind.EasyBangumi
                ? "source.easybangumi.js"
                : "source.animeko.json",
            SourceKind = preview.Kind == SourceSubscriptionKind.EasyBangumi
                ? "easybangumi-js"
                : "animeko-web-selector",
            SubscriptionId = preview.SubscriptionId,
            UpstreamPath = item.UpstreamPath,
            UpstreamRevision = item.Revision,
        };

    private static (string Key, string DisplayName) ReadEasyMetadata(
        string content)
    {
        var key = ReadMetadataValue(content, "key");
        var label = ReadMetadataValue(content, "label");
        var libVersion = ReadMetadataValue(content, "libVersion");
        if (string.IsNullOrWhiteSpace(key)
            || string.IsNullOrWhiteSpace(label)
            || !string.Equals(libVersion, "15", StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "EasyBangumi 脚本缺少 key/label 或 libVersion 不是 15。");
        }

        EasyScriptCompatibility.Validate(content);
        return (key, label);
    }

    private static (string Key, string DisplayName) ReadAnimekoMetadata(
        string content)
    {
        var definition = JsonSerializer.Deserialize<AnimekoSourceDefinition>(
            content,
            SerializerOptions)
            ?? throw new InvalidDataException("ani-subs 源配置为空。");
        AnimekoWebSource.ValidateDefinition(definition);
        return (definition.Arguments.Name, definition.Arguments.Name);
    }

    private static string ReadMetadataValue(string content, string name)
    {
        var match = EasyMetadataRegex().Match(content);
        while (match.Success)
        {
            if (string.Equals(
                    match.Groups["name"].Value,
                    name,
                    StringComparison.Ordinal))
            {
                return match.Groups["value"].Value.Trim();
            }

            match = match.NextMatch();
        }

        return string.Empty;
    }

    private async Task<SourceSubscriptionDocument> LoadAsync(
        CancellationToken cancellationToken)
    {
        if (!File.Exists(_statePath))
        {
            return new SourceSubscriptionDocument();
        }

        await using var stream = File.OpenRead(_statePath);
        var document = await JsonSerializer.DeserializeAsync<SourceSubscriptionDocument>(
            stream,
            SerializerOptions,
            cancellationToken)
            ?? throw new InvalidDataException("播放源订阅状态文件无效。");
        if (document.FormatVersion != 1)
        {
            throw new InvalidDataException("播放源订阅状态版本不受支持。");
        }

        var installed = await _installer.ListAsync(cancellationToken);
        foreach (var subscription in document.Subscriptions)
        {
            var cachedByPath = subscription.Sources.ToLookup(item => item.UpstreamPath, StringComparer.Ordinal);
            subscription.Sources.Clear();
            foreach (var package in installed.Where(package => package.IsValid && !package.IsUnmanaged
                && package.SubscriptionId == subscription.Id && package.UpstreamPath is not null))
            {
                var cached = cachedByPath[package.UpstreamPath!].FirstOrDefault(item =>
                    item.SourceId == package.Id && item.Revision == package.UpstreamRevision);
                subscription.Sources.RemoveAll(source => source.UpstreamPath == package.UpstreamPath);
                subscription.Sources.Add(new SubscriptionSourceState
                {
                    UpstreamPath = package.UpstreamPath!,
                    SourceId = package.Id,
                    DisplayName = package.DisplayName,
                    Revision = package.UpstreamRevision ?? string.Empty,
                    UpstreamBlobId = cached?.UpstreamBlobId,
                    RevisionNumber = Math.Max(1, package.Version.Build),
                    IsOrphaned = package.IsOrphaned,
                });
            }
        }
        return document;
    }

    private async Task SavePreviewCacheAsync(
        SourceSubscriptionDocument document,
        IReadOnlyDictionary<string, SubscriptionSourceState> existingByPath,
        IReadOnlyList<SubscriptionPreviewItem> items,
        CancellationToken cancellationToken)
    {
        var changed = false;
        foreach (var item in items)
        {
            if (item.Change != SubscriptionChangeKind.Unchanged
                || string.IsNullOrWhiteSpace(item.UpstreamBlobId)
                || !existingByPath.TryGetValue(item.UpstreamPath, out var previous)
                || previous.UpstreamBlobId == item.UpstreamBlobId)
            {
                continue;
            }

            previous.UpstreamBlobId = item.UpstreamBlobId;
            changed = true;
        }

        if (!changed)
        {
            return;
        }

        try
        {
            await SaveAsync(document, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "Subscription download cache was not saved; preview remains usable");
        }
    }

    private async Task SaveAsync(
        SourceSubscriptionDocument document,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_statePath)!);
        var temporaryPath = $"{_statePath}.{Guid.NewGuid():N}.tmp";
        var backupPath = $"{_statePath}.bak";
        try
        {
            await File.WriteAllTextAsync(temporaryPath,
                JsonSerializer.Serialize(document, SerializerOptions), cancellationToken);
            if (File.Exists(_statePath))
                File.Copy(_statePath, backupPath, overwrite: true);
            File.Move(temporaryPath, _statePath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    private static SourceSubscriptionKind DetectKind(string url)
    {
        var uri = new Uri(url);
        var path = uri.AbsolutePath;
        if (path.Contains(
            "EasyBangumi",
            StringComparison.OrdinalIgnoreCase)
            || path.Contains(
                "inner_source",
                StringComparison.OrdinalIgnoreCase))
        {
            return SourceSubscriptionKind.EasyBangumi;
        }

        if (path.Contains("ani-subs", StringComparison.OrdinalIgnoreCase)
            || path.Contains("subs/web", StringComparison.OrdinalIgnoreCase))
        {
            return SourceSubscriptionKind.AnimekoWeb;
        }

        throw new InvalidDataException(
            "无法识别订阅格式；请使用 EasyBangumi inner_source 或 ani-subs URL。");
    }

    private static string NormalizeUrl(string value)
    {
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidDataException("订阅 URL 必须是 HTTPS 地址。");
        }

        return uri.GetLeftPart(UriPartial.Path).TrimEnd('/');
    }

    private static string CreateStableId(string prefix, string value)
    {
        var hash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant();
        return $"{prefix}.{hash[..16]}";
    }

    private static string SanitizeId(string value)
    {
        var normalized = InvalidIdCharacterRegex().Replace(value, "-");
        return normalized.Trim('-');
    }

    [GeneratedRegex(
        @"^\s*//\s*@(?<name>[A-Za-z][A-Za-z0-9]*)\s+(?<value>.+?)\s*$",
        RegexOptions.Multiline | RegexOptions.CultureInvariant)]
    private static partial Regex EasyMetadataRegex();

    [GeneratedRegex(
        @"[^A-Za-z0-9._-]+",
        RegexOptions.CultureInvariant)]
    private static partial Regex InvalidIdCharacterRegex();
}
