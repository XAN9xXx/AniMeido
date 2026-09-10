using AniMeido.Plugin.Player.Sources.Packages;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;

namespace AniMeido.Plugin.Player.Diagnostics;

internal sealed class PlaybackDiagnosticRecorder : IAsyncDisposable
{
    internal const int MaximumSnippetLength = 64 * 1024;
    internal const long MaximumEventFileSize = 10 * 1024 * 1024;
    private const int MaximumEventCount = 5000;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = false,
    };

    private readonly object _gate = new();
    private readonly string _rootDirectory;
    private DiagnosticSession? _session;
    private string? _lastSessionDirectory;
    private string? _lastError;

    public PlaybackDiagnosticRecorder()
        : this(Path.Combine(
            SourcePackageInstaller.GetSourcesDirectory(),
            "Diagnostics"))
    {
    }

    internal PlaybackDiagnosticRecorder(string rootDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectory);
        _rootDirectory = Path.GetFullPath(rootDirectory);
    }

    public bool IsEnabled
    {
        get
        {
            lock (_gate)
            {
                return _session is not null;
            }
        }
    }

    public string? LastError
    {
        get
        {
            lock (_gate)
            {
                return _lastError;
            }
        }
    }

    public string? LastSessionDirectory
    {
        get
        {
            lock (_gate)
            {
                return _lastSessionDirectory;
            }
        }
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_session is not null)
            {
                return Task.CompletedTask;
            }

            Directory.CreateDirectory(_rootDirectory);
            var sessionDirectory = Path.Combine(
                _rootDirectory,
                DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss")
                    + $"-{Guid.NewGuid():N}");
            Directory.CreateDirectory(sessionDirectory);
            File.WriteAllText(
                Path.Combine(sessionDirectory, "README.txt"),
                """
                AniMeido PlayerPlugin playback diagnostic bundle.
                URLs have query strings removed. Cookie, Authorization,
                password, token, signature, secret and CSRF values are redacted.
                Response snippets are text-only, truncated to 64 KiB.
                """,
                Encoding.UTF8);
            var channel = Channel.CreateBounded<PlaybackDiagnosticEntry>(
                new BoundedChannelOptions(1024)
                {
                    FullMode = BoundedChannelFullMode.DropWrite,
                    SingleReader = true,
                    SingleWriter = false,
                });
            var session = new DiagnosticSession(
                sessionDirectory,
                channel);
            session.WriterTask = RunWriterAsync(session);
            _session = session;
            _lastSessionDirectory = sessionDirectory;
            _lastError = null;
        }

        Record(
            "diagnostic",
            "started",
            data: new Dictionary<string, object?>
            {
                ["pluginVersion"] =
                    typeof(PlaybackDiagnosticRecorder).Assembly
                        .GetName()
                        .Version?
                        .ToString(3),
                ["processArchitecture"] =
                    System.Runtime.InteropServices.RuntimeInformation
                        .ProcessArchitecture
                        .ToString(),
            });
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        DiagnosticSession? session;
        lock (_gate)
        {
            session = _session;
            _session = null;
        }

        if (session is null)
        {
            return;
        }

        session.Channel.Writer.TryWrite(new PlaybackDiagnosticEntry
        {
            Timestamp = DateTimeOffset.UtcNow,
            Category = "diagnostic",
            Name = "stopped",
        });
        session.Channel.Writer.TryComplete();
        await session.WriterTask.WaitAsync(cancellationToken);
    }

    public void Record(
        string category,
        string name,
        string? sourceId = null,
        Uri? uri = null,
        IReadOnlyDictionary<string, object?>? data = null,
        string? responseSnippet = null)
    {
        DiagnosticSession? session;
        lock (_gate)
        {
            session = _session;
        }

        if (session is null
            || Interlocked.Increment(ref session.EventCount)
                > MaximumEventCount)
        {
            return;
        }

        var entry = new PlaybackDiagnosticEntry
        {
            Timestamp = DateTimeOffset.UtcNow,
            Category = SanitizePlainText(category)!,
            Name = SanitizePlainText(name)!,
            SourceId = SanitizePlainText(sourceId),
            Uri = uri is null ? null : SanitizeUri(uri),
            Data = SanitizeData(data),
            ResponseSnippet = string.IsNullOrWhiteSpace(responseSnippet)
                ? null
                : SanitizeResponseSnippet(responseSnippet),
        };
        _ = session.Channel.Writer.TryWrite(entry);
    }

    public async Task ExportAsync(
        string targetPath,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(targetPath);
        await StopAsync(cancellationToken);
        string? sessionDirectory;
        lock (_gate)
        {
            sessionDirectory = _lastSessionDirectory;
        }

        if (sessionDirectory is null || !Directory.Exists(sessionDirectory))
        {
            throw new InvalidOperationException("当前没有可导出的播放诊断会话。");
        }

        var fullTargetPath = Path.GetFullPath(targetPath);
        Directory.CreateDirectory(Path.GetDirectoryName(fullTargetPath)!);
        var temporaryPath = $"{fullTargetPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            await Task.Run(
                () => ZipFile.CreateFromDirectory(
                    sessionDirectory,
                    temporaryPath,
                    CompressionLevel.Optimal,
                    includeBaseDirectory: false),
                cancellationToken);
            File.Move(temporaryPath, fullTargetPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    public async Task ClearAsync(CancellationToken cancellationToken)
    {
        await StopAsync(cancellationToken);
        if (Directory.Exists(_rootDirectory))
        {
            Directory.Delete(_rootDirectory, recursive: true);
        }

        lock (_gate)
        {
            _lastSessionDirectory = null;
            _lastError = null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync(CancellationToken.None);
    }

    internal static string SanitizeUri(Uri uri)
        => uri.IsAbsoluteUri
            ? uri.GetLeftPart(UriPartial.Path)
            : SanitizePlainText(uri.OriginalString) ?? string.Empty;

    internal static string SanitizeResponseSnippet(string value)
    {
        var truncated = value.Length <= MaximumSnippetLength
            ? value
            : value[..MaximumSnippetLength];
        var withoutQueries = Regex.Replace(
            truncated,
            @"(https?://[^\s?""'<>]+)\?[^\s""'<>]+",
            "$1?<redacted>",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
            TimeSpan.FromSeconds(1));
        var withoutSensitiveInputs = Regex.Replace(
            withoutQueries,
            @"(<input\b[^>]*\bname\s*=\s*[""']?"
                + @"(?:authorization|cookie|password|passwd|token|signature|secret|csrf)"
                + @"[""']?[^>]*\bvalue\s*=\s*[""'])([^""']*)([""'])",
            "$1<redacted>$3",
            RegexOptions.IgnoreCase
                | RegexOptions.CultureInvariant
                | RegexOptions.Singleline,
            TimeSpan.FromSeconds(1));
        return Regex.Replace(
            withoutSensitiveInputs,
            @"(?i)(authorization|cookie|password|passwd|token|signature|secret|csrf)"
                + @"([\s""'=:\-]+)([^\s""'&,<>]+)",
            "$1$2<redacted>",
            RegexOptions.CultureInvariant,
            TimeSpan.FromSeconds(1));
    }

    private async Task RunWriterAsync(DiagnosticSession session)
    {
        try
        {
            var eventsPath = Path.Combine(
                session.Directory,
                "events.jsonl");
            await using var stream = new FileStream(
                eventsPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.Read,
                bufferSize: 16 * 1024,
                useAsync: true);
            await using var writer = new StreamWriter(
                stream,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            await foreach (var entry in session.Channel.Reader.ReadAllAsync())
            {
                var line = JsonSerializer.Serialize(entry, SerializerOptions);
                var estimatedBytes = Encoding.UTF8.GetByteCount(line) + 1;
                if (stream.Position + estimatedBytes
                    > MaximumEventFileSize)
                {
                    var limitEntry = JsonSerializer.Serialize(
                        new PlaybackDiagnosticEntry
                        {
                            Timestamp = DateTimeOffset.UtcNow,
                            Category = "diagnostic",
                            Name = "size-limit-reached",
                        },
                        SerializerOptions);
                    await writer.WriteLineAsync(limitEntry);
                    break;
                }

                await writer.WriteLineAsync(line);
                await writer.FlushAsync();
            }
        }
        catch (Exception ex)
            when (ex is IOException
                or UnauthorizedAccessException
                or JsonException)
        {
            lock (_gate)
            {
                _lastError = SanitizePlainText(ex.Message);
            }
        }
    }

    private static Dictionary<string, object?>? SanitizeData(
        IReadOnlyDictionary<string, object?>? data)
    {
        if (data is null || data.Count == 0)
        {
            return null;
        }

        var result = new Dictionary<string, object?>(
            StringComparer.Ordinal);
        foreach (var (key, value) in data)
        {
            var sanitizedKey = SanitizePlainText(key) ?? string.Empty;
            result[sanitizedKey] = IsSensitiveName(key)
                ? "<redacted>"
                : value switch
                {
                    Uri uri => SanitizeUri(uri),
                    string text => SanitizeResponseSnippet(text),
                    _ => value,
                };
        }

        return result;
    }

    private static bool IsSensitiveName(string name)
        => name.Contains("cookie", StringComparison.OrdinalIgnoreCase)
            || name.Contains("authorization", StringComparison.OrdinalIgnoreCase)
            || name.Contains("password", StringComparison.OrdinalIgnoreCase)
            || name.Contains("token", StringComparison.OrdinalIgnoreCase)
            || name.Contains("signature", StringComparison.OrdinalIgnoreCase)
            || name.Contains("secret", StringComparison.OrdinalIgnoreCase)
            || name.Contains("csrf", StringComparison.OrdinalIgnoreCase);

    private static string? SanitizePlainText(string? value)
        => value is null
            ? null
            : value.Replace("\r", " ", StringComparison.Ordinal)
                .Replace("\n", " ", StringComparison.Ordinal);

    private sealed class DiagnosticSession
    {
        public DiagnosticSession(
            string directory,
            Channel<PlaybackDiagnosticEntry> channel)
        {
            Directory = directory;
            Channel = channel;
        }

        public string Directory { get; }

        public Channel<PlaybackDiagnosticEntry> Channel { get; }

        public Task WriterTask { get; set; } = Task.CompletedTask;

        public int EventCount;
    }

    private sealed class PlaybackDiagnosticEntry
    {
        public DateTimeOffset Timestamp { get; set; }

        public string Category { get; set; } = string.Empty;

        public string Name { get; set; } = string.Empty;

        public string? SourceId { get; set; }

        public string? Uri { get; set; }

        public Dictionary<string, object?>? Data { get; set; }

        public string? ResponseSnippet { get; set; }
    }
}
