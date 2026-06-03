namespace AniMeido.Contracts;

/// <summary>
/// 应用数据路径信息。用于需要访问数据库、备份、日志目录的场景。
/// 由 App 层实现并注入，避免各模块重复定义路径规则。
/// </summary>
public interface IAppDataPaths
{
    string DatabasePath { get; }
    string BackupDirectory { get; }
    string LogDirectory { get; }
}
