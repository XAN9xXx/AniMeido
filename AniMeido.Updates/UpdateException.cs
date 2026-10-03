namespace AniMeido.Updates;

public enum UpdateErrorKind
{
    InvalidVersion,
    InvalidManifest,
    ManifestTooLarge,
    SignatureTooLarge,
    InvalidSignature,
    Network,
    HashMismatch,
    PackageTooLarge,
    FileSystem,
    InvalidArchive,
    ArchiveLimitExceeded,
    RequiredFileMissing,
    TargetExists
}

public sealed class UpdateException : Exception
{
    public UpdateErrorKind Kind { get; }

    public UpdateException(UpdateErrorKind kind, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        Kind = kind;
    }
}
