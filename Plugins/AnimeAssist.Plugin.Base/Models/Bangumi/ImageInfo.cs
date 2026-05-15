namespace AnimeAssist.Plugin.Base.Models.Bangumi
{
    /// <summary>
    /// 标注了使用图片的尺寸信息。
    /// </summary>
    /// <remarks>
    /// 包含大图（Large）、常规图（Common）、中图（Medium）、小图（Small）和网格图（Grid）的URL。 
    /// ||
    /// 参数列表：string? Large, string? Common, string? Medium, string? Small, string? Grid
    /// </remarks>
    /// <param name="Large">大图。</param>
    /// <param name="Common">常规图。</param>
    /// <param name="Medium">中图。</param>
    /// <param name="Small">小图。</param>
    /// <param name="Grid">网格图。</param>
    internal record ImageInfo(string? Large, string? Common, string? Medium, string? Small, string? Grid);

}
