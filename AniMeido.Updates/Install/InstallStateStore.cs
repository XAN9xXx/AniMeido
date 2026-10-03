using System.Diagnostics;
using System.Text.Json;

namespace AniMeido.Updates.Install;

/// <summary>所有状态访问持有同一个跨进程文件锁；tmp 不是提交点，正式文件才是。</summary>
public sealed class InstallStateStore
{
    private readonly TimeSpan _lockTimeout;
    private readonly Action<string>? _log;
    private readonly Action<InstallWriteStage>? _writeCheckpoint;

    public string Root { get; }
    public string StatePath => Path.Combine(Root, "state.json");

    public InstallStateStore(string root, TimeSpan? lockTimeout = null, Action<string>? log = null)
        : this(root, lockTimeout, log, null)
    {
    }

    internal InstallStateStore(string root, TimeSpan? lockTimeout, Action<string>? log,
        Action<InstallWriteStage>? writeCheckpoint)
    {
        Root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        _lockTimeout = lockTimeout ?? TimeSpan.FromSeconds(3);
        if (_lockTimeout < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(lockTimeout));
        }
        _log = log;
        _writeCheckpoint = writeCheckpoint;
    }

    public InstallState Read() => WithLock(ReadLocked);

    public void Write(InstallState state) => WithLock(() =>
    {
        WriteLocked(state);
        return true;
    });

    internal T WithLock<T>(Func<T> operation)
    {
        // 不创建根目录：根不存在不是可恢复的安装，也不能误创建真实安装位置。
        var timer = Stopwatch.StartNew();
        FileStream fileLock;
        while (true)
        {
            try
            {
                fileLock = new FileStream(Path.Combine(Root, "state.lock"), FileMode.OpenOrCreate,
                    FileAccess.ReadWrite, FileShare.None);
                break;
            }
            catch (IOException ex) when (ex is not DirectoryNotFoundException)
            {
                if (timer.Elapsed >= _lockTimeout)
                {
                    throw new InstallException("等待 state.lock 超时；另一个 AniMeido 进程可能正在切换版本。", ex);
                }
                Thread.Sleep((int)Math.Min(50, Math.Max(1, (_lockTimeout - timer.Elapsed).TotalMilliseconds)));
            }
        }

        using (fileLock)
        {
            return operation();
        }
    }

    internal InstallState ReadExistingLocked()
    {
        using var stream = File.OpenRead(StatePath);
        var state = JsonSerializer.Deserialize(stream, InstallJsonContext.Default.InstallState)
            ?? throw new InstallException("state.json 为空。");
        state.Validate();
        if (!InstallLayout.IsValidVersion(Root, state.Current))
        {
            throw new InstallException($"当前版本 {state.Current} 缺少 {InstallLayout.AppFileName}。");
        }
        return state;
    }

    internal InstallState ReadLocked()
    {
        InstallState state;
        try
        {
            state = ReadExistingLocked();
        }
        catch (Exception ex) when (ex is FileNotFoundException or JsonException or InstallException)
        {
            var versions = Directory.EnumerateDirectories(Root, "app-*", SearchOption.TopDirectoryOnly)
                .Select(Path.GetFileName)
                .Where(name => name is not null && InstallLayout.TryGetDirectoryVersion(name, out _))
                .Select(name => name![4..])
                .Where(version => InstallLayout.IsValidVersion(Root, version))
                .OrderByDescending(SemanticVersion.Parse)
                .ThenByDescending(version => version, StringComparer.Ordinal)
                .ToArray();
            if (versions.Length == 0)
            {
                throw new InstallException("安装状态无法读取，且没有包含 AniMeido.App.exe 的可用 app-<版本> 目录。", ex);
            }
            state = new InstallState { Current = versions[0], CurrentHealthy = true };
            WriteLocked(state);
            _log?.Invoke($"安装状态恢复为 {state.Current}（健康状况未知，按健康处理）。原因：{ex.Message}");
        }

        // 正式文件完整才清理残留；删除失败不能使一次成功读取变成启动失败。
        try
        {
            File.Delete(StatePath + ".tmp");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _log?.Invoke($"无法清理 state.json.tmp：{ex.Message}");
        }
        return state;
    }

    internal void WriteLocked(InstallState state)
    {
        state.Validate();
        if (!InstallLayout.IsValidVersion(Root, state.Current))
        {
            throw new InstallException($"不能保存指向无效版本目录的状态：{state.Current}。");
        }
        _writeCheckpoint?.Invoke(InstallWriteStage.BeforeTemporaryWrite);
        using (var stream = new FileStream(StatePath + ".tmp", FileMode.Create,
            FileAccess.Write, FileShare.None))
        {
            JsonSerializer.Serialize(stream, state, InstallJsonContext.Default.InstallState);
            _writeCheckpoint?.Invoke(InstallWriteStage.AfterTemporaryWrite);
            stream.Flush(flushToDisk: true);
        }
        _writeCheckpoint?.Invoke(InstallWriteStage.BeforeReplace);
        File.Move(StatePath + ".tmp", StatePath, overwrite: true);
        _writeCheckpoint?.Invoke(InstallWriteStage.AfterReplace);
    }
}

internal enum InstallWriteStage
{
    BeforeTemporaryWrite,
    AfterTemporaryWrite,
    BeforeReplace,
    AfterReplace,
}
