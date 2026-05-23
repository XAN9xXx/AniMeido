namespace AniMeido.Contracts
{
    public static class AppServices
    {
        // TODO: 待重构的反模式
        /// <summary>
        /// 服务定位器
        /// </summary>
        public static IServiceProvider? Provider { get; set; }
    }
}
