using AniMeido.Contracts.Playback;
using System.Reflection;
using System.Runtime.Loader;

namespace AniMeido.Plugin.Player.Sources.Managed;

internal sealed class ManagedSourceLoadContext : AssemblyLoadContext
{
    private static readonly Assembly PlayerAssembly =
        typeof(IOnlineAnimeSource).Assembly;
    private static readonly Assembly ContractsAssembly =
        typeof(AnimePlaybackContext).Assembly;
    private readonly string _sourceDirectory;

    public ManagedSourceLoadContext(string sourceDirectory)
        : base(isCollectible: false)
    {
        _sourceDirectory = sourceDirectory;
    }

    protected override Assembly? Load(AssemblyName assemblyName)
    {
        if (string.Equals(
            assemblyName.Name,
            PlayerAssembly.GetName().Name,
            StringComparison.OrdinalIgnoreCase))
        {
            return PlayerAssembly;
        }

        if (string.Equals(
            assemblyName.Name,
            ContractsAssembly.GetName().Name,
            StringComparison.OrdinalIgnoreCase))
        {
            return ContractsAssembly;
        }

        var candidate = Path.Combine(
            _sourceDirectory,
            assemblyName.Name + ".dll");
        return File.Exists(candidate)
            ? LoadFromAssemblyPath(candidate)
            : null;
    }
}
