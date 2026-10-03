namespace AniMeido.Updates;

/// <summary>只由共享校验器创建，不可修改的清单。</summary>
public sealed class UpdateManifest
{
    internal UpdateManifest(string channel, SemanticVersion version, DateOnly releaseDate,
        SemanticVersion minUpdatableFrom, string? notes, UpdatePackage package)
    {
        Channel = channel;
        Version = version;
        ReleaseDate = releaseDate;
        MinUpdatableFrom = minUpdatableFrom;
        Notes = notes;
        Package = package;
    }

    public int Schema => 1;
    public string Channel { get; }
    public SemanticVersion Version { get; }
    public DateOnly ReleaseDate { get; }
    public SemanticVersion MinUpdatableFrom { get; }
    public string? Notes { get; }
    public UpdatePackage Package { get; }
}

public sealed class UpdatePackage
{
    internal UpdatePackage(Uri[] urls, long size, string sha256)
    {
        Urls = Array.AsReadOnly(urls);
        Size = size;
        Sha256 = sha256;
    }

    public IReadOnlyList<Uri> Urls { get; }
    public long Size { get; }
    public string Sha256 { get; }
}

public enum UpdateDecision
{
    NoUpdate,
    UpdateAvailable,
    ReinstallRequired
}

public static class UpdatePolicy
{
    public static UpdateDecision Evaluate(UpdateManifest manifest, SemanticVersion currentVersion)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(currentVersion);
        if (manifest.Version.CompareTo(currentVersion) <= 0)
        {
            return UpdateDecision.NoUpdate;
        }

        return currentVersion.CompareTo(manifest.MinUpdatableFrom) < 0
            ? UpdateDecision.ReinstallRequired
            : UpdateDecision.UpdateAvailable;
    }
}
