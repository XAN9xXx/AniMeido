using Microsoft.Data.Sqlite;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace AniMeido.Updates.Tests;

internal static class RestoreDatabaseFixture
{
    internal static string MakeDatabase(InstallTestRoot temp, string relative, int version)
    {
        var path = temp.PathFor(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var connection = OpenDatabase(path);
        using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA user_version = {version}";
        command.ExecuteNonQuery();
        return path;
    }

    internal static string MakeSource(InstallTestRoot temp, int version, bool beforeRestore = false, string dataDirectory = "data")
    {
        var name = beforeRestore ? "AniMeido-before-restore-20261019-120000" : "AniMeido-rollback-from-2.2.0-20261019-120000";
        var database = MakeDatabase(temp, dataDirectory + "/Backups/update/" + name + "/AniMeido.db", version);
        return Path.GetDirectoryName(database)!;
    }

    internal static string MakeWalSource(InstallTestRoot temp, int baseVersion, int walVersion, bool includeSharedMemory, string dataDirectory = "data")
    {
        var generator = MakeDatabase(temp, "wal-generator.db", baseVersion);
        var directory = MakeSource(temp, baseVersion, dataDirectory: dataDirectory);
        var destination = Path.Combine(directory, "AniMeido.db");
        using var connection = OpenDatabase(generator);
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            PRAGMA journal_mode = WAL;
            PRAGMA wal_autocheckpoint = 0;
            PRAGMA user_version = {walVersion};
            CREATE TABLE wal_evidence (Value TEXT);
            INSERT INTO wal_evidence VALUES ('committed in WAL');
            """;
        command.ExecuteNonQuery();
        File.Copy(generator, destination, overwrite: true);
        File.Copy(generator + "-wal", destination + "-wal", overwrite: true);
        if (includeSharedMemory)
            File.Copy(generator + "-shm", destination + "-shm", overwrite: true);
        return directory;
    }

    internal static SqliteConnection OpenDatabase(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Pooling = false,
        }.ToString());
        connection.Open();
        return connection;
    }

    internal static void WriteSchemaAssembly(string path, int version)
    {
        var metadata = new MetadataBuilder();
        metadata.AddModule(0, metadata.GetOrAddString("SyntheticSchema"), metadata.GetOrAddGuid(Guid.NewGuid()), default, default);
        metadata.AddTypeDefinition(TypeAttributes.NotPublic, default, metadata.GetOrAddString("<Module>"), default,
            MetadataTokens.FieldDefinitionHandle(1), MetadataTokens.MethodDefinitionHandle(1));
        var field = metadata.AddFieldDefinition(FieldAttributes.Public | FieldAttributes.Static | FieldAttributes.Literal | FieldAttributes.HasDefault,
            metadata.GetOrAddString("CurrentVersion"), metadata.GetOrAddBlob(new byte[] { 0x06, 0x08 }));
        metadata.AddConstant(field, version);
        metadata.AddTypeDefinition(TypeAttributes.Public, metadata.GetOrAddString("AniMeido.Plugin.Base.Services"),
            metadata.GetOrAddString("DatabaseSchema"), default, MetadataTokens.FieldDefinitionHandle(1), MetadataTokens.MethodDefinitionHandle(1));
        var image = new ManagedPEBuilder(PEHeaderBuilder.CreateLibraryHeader(), new MetadataRootBuilder(metadata), new BlobBuilder());
        var blob = new BlobBuilder();
        image.Serialize(blob);
        File.WriteAllBytes(path, blob.ToArray());
    }
}
