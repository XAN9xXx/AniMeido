namespace AniMeido.Plugin.Base.Services;

public sealed class ArchiveBundleRollbackException : Exception
{
    public ArchiveBundleRollbackException(string backupPath, Exception importException, Exception rollbackException)
        : base($"导入未完成，且未能撤销已写入的部分。已暂停修改，请先从备份恢复后再导入。导入前的数据备份在 {backupPath}。",
            new AggregateException(importException, rollbackException))
    {
    }
}
