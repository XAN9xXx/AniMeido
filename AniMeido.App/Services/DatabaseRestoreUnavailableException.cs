namespace AniMeido.App.Services;

/// <summary>备份恢复因暂时性的文件访问或 SQLite 占用失败而中止，不能改用更旧的备份。</summary>
public sealed class DatabaseRestoreUnavailableException : Exception
{
    public DatabaseRestoreUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
