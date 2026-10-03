using System.Text;
using AniMeido.Updates.Install;

namespace AniMeido.Updates.Tests;

internal sealed class InstallTestRoot : IDisposable
{
    private readonly TemporaryDirectory _temp = new();
    internal string Root => _temp.Root;
    internal InstallStateStore Store { get; }
    internal InstallManager Manager { get; }
    internal List<string> Logs { get; } = [];

    internal InstallTestRoot()
    {
        AddVersion("2.2.0");
        Store = new InstallStateStore(Root, log: Logs.Add);
        Manager = new InstallManager(Store, Logs.Add);
    }

    internal string PathFor(string name) => Path.Combine(Root, name);

    internal string AddVersion(string version, bool withExecutable = true)
    {
        var directory = PathFor("app-" + version);
        Directory.CreateDirectory(directory);
        if (withExecutable)
        {
            File.WriteAllText(Path.Combine(directory, InstallLayout.AppFileName), "test-only, never executed");
        }
        return directory;
    }

    internal InstallState Save(bool healthy = true, int failed = 0, string? previous = null,
        string? pending = null, MigrationRecord? migration = null)
    {
        var state = new InstallState
        {
            Current = "2.2.0",
            CurrentHealthy = healthy,
            FailedStarts = failed,
            Previous = previous,
            Pending = pending,
            Migration = migration,
        };
        Store.Write(state);
        return state;
    }

    internal string MakeDatabase(string name, string payload)
    {
        var path = PathFor(name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, Encoding.ASCII.GetBytes("SQLite format 3\0" + payload));
        return path;
    }

    public void Dispose() => _temp.Dispose();
}
