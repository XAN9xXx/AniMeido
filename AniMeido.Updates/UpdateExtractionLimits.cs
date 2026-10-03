namespace AniMeido.Updates;

public sealed class UpdateExtractionLimits
{
    public int MaxEntries { get; init; } = 20000;
    public long MaxFileBytes { get; init; } = 1024L * 1024 * 1024;
    public long MaxTotalBytes { get; init; } = 4L * 1024 * 1024 * 1024;
}
