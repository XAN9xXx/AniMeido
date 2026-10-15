namespace AniMeido.Plugin.Base.Exceptions
{
    public sealed class BangumiApiException : Exception
    {
        public BangumiApiException() : base() { }

        public BangumiApiException(string message) : base(message) { }

        public BangumiApiException(string message, Exception innerException)
            : base(message, innerException){}
    }
}
