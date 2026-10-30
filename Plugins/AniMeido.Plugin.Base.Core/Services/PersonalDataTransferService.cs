namespace AniMeido.Plugin.Base.Services;

/// <summary>
/// 数据管理页的完整操作入口：准入、备份与提交在同一业务作用域内，
/// 同步 SQLite 和文件工作由此离开 UI 线程。界面不编排内部步骤。
/// </summary>
public sealed class PersonalDataTransferService(
    SqliteConnectionFactory database,
    ExportService export,
    BackupService backup,
    ArchiveBundleService bundle)
{
    public Task<string> BackupAsync() => Task.Run(backup.BackupAsync);

    public Task<string> ExportJsonAsync() => Task.Run(export.ExportAsync);

    public Task<ExportData?> PreviewJsonAsync(string json, CancellationToken ct = default)
        => Task.Run(() => ExportService.Preview(json), ct);

    public Task ExportBundleAsync(string destinationPath, CancellationToken ct = default)
        => Task.Run(() => bundle.ExportAsync(destinationPath, ct), ct);

    public Task<int> ImportBundleAsync(string bundlePath, CancellationToken ct = default)
        => Task.Run(() => bundle.ImportAsync(bundlePath, ct), ct);

    public Task<(int trackingCount, int configCount, int tagCount, int actionCenterCount)> ImportJsonAsync(
        string json, CancellationToken ct = default)
        => Task.Run(async () =>
        {
            using var operation = database.Access.BeginExclusive();
            await operation.WaitForDrainAsync(ct).ConfigureAwait(false);
            var prepared = await export.PrepareImportAsync(json, ct: ct).ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();
            await backup.BackupAsync().ConfigureAwait(false);
            // 单事务导入失败由 SQLite 回滚，不用整库恢复覆盖其他状态。
            return await export.ImportPreparedAsync(prepared.Data).ConfigureAwait(false);
        }, ct);
}
