namespace AniMeido.Plugin.Base.Exceptions;

public sealed class MetadataServiceException : Exception
{
    public MetadataServiceException() { }
    public MetadataServiceException(string message) : base(message) { }
    public MetadataServiceException(string message, Exception innerException)
        : base(message, innerException) { }
}
