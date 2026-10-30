using System.Globalization;
using AniMeido.Contracts;
using AniMeido.Updates;
using AniMeido.Updates.Install;

namespace AniMeido.App.Services;

internal sealed record RollbackDataEntry(string Path, string Version, DateTime Time, bool IsBeforeRestore = false)
{
    public string DisplayText => IsBeforeRestore ? $"恢复前数据 | 时间：{Time:yyyy-MM-dd HH:mm:ss}"
        : $"回滚前数据 | 来源版本：{Version} | 时间：{Time:yyyy-MM-dd HH:mm:ss}";
}

/// <summary>只管理回滚/恢复时另存的数据；不直接修改当前数据库。</summary>
public sealed class RollbackDataStore : IAsyncDisposable
{
    private const string Prefix = RollbackDataDirectory.RollbackPrefix;
    private readonly string _root;
    private readonly Action? _beforeDelete;
    private readonly object _lifetimeGate = new();
    private readonly HashSet<Task> _pending = [];
    private bool _stopping;
    private Task? _stopTask;

    internal string DataDirectory { get; }

    public RollbackDataStore(IAppDataPaths paths)
    {
        _root = Path.GetFullPath(Path.Combine(paths.BackupDirectory, "update"));
        DataDirectory = Path.GetDirectoryName(paths.DatabasePath)!;
    }

    internal RollbackDataStore(IAppDataPaths paths, Action beforeDelete) : this(paths)
        => _beforeDelete = beforeDelete;

    internal Task<IReadOnlyList<RollbackDataEntry>> ListAsync() => RunAsync(ListCore);

    internal Task<string> ValidateDirectoryAsync(string directory)
        => RunAsync(() => ValidateDirectoryCore(directory));

    internal Task DeleteAsync(string directory) => RunAsync(() =>
    {
        DeleteCore(directory);
        return true;
    });

    private IReadOnlyList<RollbackDataEntry> ListCore()
    {
        if (!Directory.Exists(_root))
            return [];
        RollbackDataDirectory.CheckAncestors(_root);
        return Directory.EnumerateDirectories(_root, "*", SearchOption.TopDirectoryOnly)
            .Where(directory => RollbackDataDirectory.IsArchiveName(Path.GetFileName(directory)))
            .Select(ReadEntry)
            .OrderByDescending(entry => entry.Time).ToArray();
    }

    private string ValidateDirectoryCore(string directory)
        => RollbackDataDirectory.Validate(_root, directory);

    private void DeleteCore(string directory)
    {
        var full = ValidateDirectoryCore(directory);
        CheckTree(full);
        _beforeDelete?.Invoke();
        Directory.Delete(full, recursive: true);
    }

    private RollbackDataEntry ReadEntry(string directory)
    {
        var full = ValidateDirectoryCore(directory);
        var fileName = Path.GetFileName(full);
        if (fileName.StartsWith(RollbackDataDirectory.RestorePrefix, StringComparison.Ordinal))
        {
            var suffix = fileName[RollbackDataDirectory.RestorePrefix.Length..];
            var time = suffix.Length >= 15 && DateTime.TryParseExact(suffix[..15], "yyyyMMdd-HHmmss",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
                ? parsed : Directory.GetCreationTime(full);
            return new(full, "", time, IsBeforeRestore: true);
        }
        var name = fileName[Prefix.Length..];
        // 目录后缀：<SemVer>-yyyyMMdd-HHmmss[-碰撞时的 GUID]；预览版号自身可以含 '-'。
        for (var i = 0; i + 16 <= name.Length; i++)
        {
            if (name[i] == '-' && DateTime.TryParseExact(name.Substring(i + 1, 15), "yyyyMMdd-HHmmss",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out var time)
                && SemanticVersion.TryParse(name[..i], out _)
                && (i + 16 == name.Length || name[i + 16] == '-'))
                return new(full, name[..i], time);
        }
        return new(full, name, Directory.GetCreationTime(full));
    }

    private async Task<T> RunAsync<T>(Func<T> action)
    {
        Task<T> operation;
        lock (_lifetimeGate)
        {
            ObjectDisposedException.ThrowIf(_stopping, this);
            operation = Task.Run(action);
            _pending.Add(operation);
        }
        try
        {
            return await operation.ConfigureAwait(false);
        }
        finally
        {
            lock (_lifetimeGate)
                _pending.Remove(operation);
        }
    }

    public ValueTask DisposeAsync()
    {
        lock (_lifetimeGate)
        {
            _stopping = true;
            _stopTask ??= WaitForOperationsAsync(_pending.ToArray());
            return new ValueTask(_stopTask);
        }
    }

    private static async Task WaitForOperationsAsync(Task[] pending)
    {
        // 操作失败由原调用者处理，退出只等待实际文件工作收尾。
        await Task.WhenAll(pending).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
    }

    private static void CheckTree(string directory)
    {
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
        {
            if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0)
                throw new IOException($"拒绝删除包含符号链接或 junction 的目录：{entry}");
            if (Directory.Exists(entry))
                CheckTree(entry);
        }
    }
}
