namespace AniMeido.Plugin.Base.Exceptions;

public sealed class MetadataServiceException : Exception
{
    public const string RateLimitMessage = "请求较多，请稍后重试。";

    internal string? Code { get; private init; }
    internal static MetadataServiceException SearchFailure(string code, string message) => new(message) { Code = code };

    public bool IsRateLimited { get; private init; }

    internal static MetadataServiceException RateLimited()
        => new(RateLimitMessage) { IsRateLimited = true };

    public MetadataServiceException() { }
    public MetadataServiceException(string message) : base(message) { }
    public MetadataServiceException(string message, Exception innerException)
        : base(message, innerException) { }
}
