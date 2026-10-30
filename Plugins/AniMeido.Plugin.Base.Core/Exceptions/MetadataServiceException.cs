namespace AniMeido.Plugin.Base.Exceptions;

public sealed class MetadataServiceException : Exception
{
    public bool IsRateLimited { get; private init; }

    internal static MetadataServiceException RateLimited()
        => new("请求较多，请稍后重试。") { IsRateLimited = true };

    public MetadataServiceException() { }
    public MetadataServiceException(string message) : base(message) { }
    public MetadataServiceException(string message, Exception innerException)
        : base(message, innerException) { }
}
