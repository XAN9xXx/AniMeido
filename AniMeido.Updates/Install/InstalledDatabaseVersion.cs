using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace AniMeido.Updates.Install;

/// <summary>只读目标版本的托管元数据；不加载 Base 程序集，也不依赖 SQLite 或运行时反射。</summary>
public static class InstalledDatabaseVersion
{
    public static int Read(string appExecutable)
    {
        var path = Path.Combine(Path.GetDirectoryName(appExecutable)!, "AniMeido.Plugin.Base.dll");
        RollbackDataDirectory.CheckAncestors(path);
        using var stream = File.OpenRead(path);
        using var pe = new PEReader(stream);
        var metadata = pe.GetMetadataReader();
        foreach (var handle in metadata.TypeDefinitions)
        {
            var type = metadata.GetTypeDefinition(handle);
            if (!metadata.StringComparer.Equals(type.Namespace, "AniMeido.Plugin.Base.Services")
                || !metadata.StringComparer.Equals(type.Name, "DatabaseSchema"))
                continue;
            foreach (var fieldHandle in type.GetFields())
            {
                var field = metadata.GetFieldDefinition(fieldHandle);
                if (!metadata.StringComparer.Equals(field.Name, "CurrentVersion") || field.GetDefaultValue().IsNil)
                    continue;
                var constant = metadata.GetConstant(field.GetDefaultValue());
                if (constant.TypeCode != ConstantTypeCode.Int32)
                    break;
                var value = metadata.GetBlobReader(constant.Value).ReadInt32();
                if (value >= 0)
                    return value;
            }
        }
        throw new InstallException("无法读取当前程序的 DatabaseSchema.CurrentVersion，未恢复数据库。");
    }
}
