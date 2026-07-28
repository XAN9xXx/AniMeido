using AniMeido.Plugin.Player.Sources.Packages;
using Microsoft.Web.WebView2.Core;
using System.Text.Json;

namespace AniMeido.Plugin.Player.Sources.Web;

internal sealed class HostWebSessionManager
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
    };

    private readonly string _userDataDirectory;
    private readonly string? _metadataPath;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private CoreWebView2Environment? _environment;

    public HostWebSessionManager()
        : this(SourcePackageInstaller.GetSourcesDirectory())
    {
    }

    internal HostWebSessionManager(string sourcesDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcesDirectory);
        _userDataDirectory = Path.Combine(sourcesDirectory, "WebView2");
        _metadataPath = Path.Combine(
            sourcesDirectory,
            "Sessions",
            "metadata.json");
    }

    public async Task<CoreWebView2Environment> GetEnvironmentAsync()
    {
        await _gate.WaitAsync();
        try
        {
            if (_environment is not null)
            {
                return _environment;
            }

            Directory.CreateDirectory(_userDataDirectory);
            _environment =
                await CoreWebView2Environment.CreateWithOptionsAsync(
                    string.Empty,
                    _userDataDirectory,
                    new CoreWebView2EnvironmentOptions());
            return _environment;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<IReadOnlyList<HostWebSessionMetadata>> ListAsync(
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            return (await ReadCoreAsync(cancellationToken))
                .OrderByDescending(item => item.LastVerifiedAt)
                .ToArray();
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RecordVerifiedAsync(
        string host,
        string sourceId,
        string? userAgent,
        CancellationToken cancellationToken)
    {
        var normalizedHost = NormalizeHost(host);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var items = (await ReadCoreAsync(cancellationToken)).ToList();
            var existing = items.FirstOrDefault(item =>
                string.Equals(
                    item.Host,
                    normalizedHost,
                    StringComparison.OrdinalIgnoreCase));
            if (existing is null)
            {
                existing = new HostWebSessionMetadata
                {
                    Host = normalizedHost,
                };
                items.Add(existing);
            }

            existing.UserAgent = string.IsNullOrWhiteSpace(userAgent)
                ? existing.UserAgent
                : userAgent;
            existing.SourceIds = existing.SourceIds
                .Append(sourceId)
                .Where(value => !string.IsNullOrWhiteSpace(value))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(value => value, StringComparer.Ordinal)
                .ToArray();
            existing.LastVerifiedAt = DateTimeOffset.UtcNow;
            await WriteCoreAsync(items, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RemoveMetadataAsync(
        string? host,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_metadataPath is null)
            {
                return;
            }

            if (host is null)
            {
                await WriteCoreAsync([], cancellationToken);
                return;
            }

            var normalizedHost = NormalizeHost(host);
            var remaining = (await ReadCoreAsync(cancellationToken))
                .Where(item => !string.Equals(
                    item.Host,
                    normalizedHost,
                    StringComparison.OrdinalIgnoreCase))
                .ToArray();
            await WriteCoreAsync(remaining, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    internal static string NormalizeHost(string host)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(host);
        var normalized = host.Trim().TrimEnd('.').ToLowerInvariant();
        return normalized.StartsWith("www.", StringComparison.Ordinal)
            ? normalized[4..]
            : normalized;
    }

    private async Task<IReadOnlyList<HostWebSessionMetadata>> ReadCoreAsync(
        CancellationToken cancellationToken)
    {
        if (_metadataPath is null || !File.Exists(_metadataPath))
        {
            return [];
        }

        try
        {
            await using var stream = File.OpenRead(_metadataPath);
            return await JsonSerializer.DeserializeAsync<
                    List<HostWebSessionMetadata>>(
                    stream,
                    SerializerOptions,
                    cancellationToken)
                ?? [];
        }
        catch (Exception ex)
            when (ex is JsonException
                or IOException
                or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private async Task WriteCoreAsync(
        IReadOnlyList<HostWebSessionMetadata> items,
        CancellationToken cancellationToken)
    {
        if (_metadataPath is null)
        {
            return;
        }

        var directory = Path.GetDirectoryName(_metadataPath)!;
        Directory.CreateDirectory(directory);
        var temporaryPath = $"{_metadataPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            await File.WriteAllTextAsync(
                temporaryPath,
                JsonSerializer.Serialize(items, SerializerOptions),
                cancellationToken);
            File.Move(temporaryPath, _metadataPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }
}

internal sealed class HostWebSessionMetadata
{
    public string Host { get; set; } = string.Empty;

    public string? UserAgent { get; set; }

    public IReadOnlyList<string> SourceIds { get; set; } = [];

    public DateTimeOffset LastVerifiedAt { get; set; }

    public override string ToString()
        => $"{Host} · {string.Join(", ", SourceIds)} · "
            + $"{LastVerifiedAt.ToLocalTime():yyyy-MM-dd HH:mm}";
}
