namespace AniMeido.Plugin.Player.Sources.Web;

internal enum SourceResolutionFailureKind
{
    Timeout,
    Authentication,
    HumanVerification,
    RateLimited,
    AccessDenied,
    NotFound,
    RuleMismatch,
    MediaRejected,
}

internal sealed class SourceResolutionException : Exception
{
    public SourceResolutionException(
        SourceResolutionFailureKind kind,
        string message,
        Uri? pageUri = null,
        Exception? innerException = null)
        : base(message, innerException)
    {
        Kind = kind;
        PageUri = pageUri;
    }

    public SourceResolutionFailureKind Kind { get; }

    public Uri? PageUri { get; }
}
