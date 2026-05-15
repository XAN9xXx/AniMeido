namespace AnimeAssist.Plugin.Base.Exceptions
{
    /// <summary>
    /// 用于抛出与Bangumi API相关的异常，提供更具体的错误信息以便调试和错误处理。
    /// </summary>
    public sealed class BangumiApiException : Exception
    {
        /// <summary>
        /// 初始化BangumiApiException的新实例。
        /// </summary>
        public BangumiApiException() : base() { }

        /// <summary>
        /// 使用指定的错误消息初始化BangumiApiException的新实例。
        /// </summary>
        /// <param name="message">异常消息。</param>
        public BangumiApiException(string message) : base(message) { }

        /// <summary>
        /// 使用指定的错误消息和内部异常初始化BangumiApiException的新实例。
        /// </summary>
        /// <param name="message">异常消息。</param>
        /// <param name="innerException">内部异常。</param>
        public BangumiApiException(string message, Exception innerException)
            : base(message, innerException){}
    }
}
