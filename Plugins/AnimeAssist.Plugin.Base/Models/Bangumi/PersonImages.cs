namespace AniMeido.Plugin.Base.Models.Bangumi
{
    /// <summary>
    /// 人物或公司头像的多尺寸URL。
    /// </summary>
    /// <param name="Small">小图。</param>
    /// <param name="Medium">中图。</param>
    /// <param name="Large">大图。</param>
    /// <param name="Grid">网格图。</param>
    internal record PersonImages(string? Small, string? Medium, string? Large, string? Grid);

}
