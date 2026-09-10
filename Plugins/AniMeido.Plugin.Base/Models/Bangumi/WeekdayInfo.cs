namespace AniMeido.Plugin.Base.Models.Bangumi
{
    /// <summary>
    /// 标注了每周的星期几信息。
    /// </summary>
    /// <remarks>
    /// 包含英文、中文、日文名称和对应的ID（1-7，分别对应周一到周日）。
    /// </remarks>
    /// <param name="En">英文名称。</param>
    /// <param name="Cn">中文名称。</param>
    /// <param name="Ja">日文名称。</param>
    /// <param name="Id">数字标识。</param>
    internal record WeekdayInfo(string? En, string? Cn, string? Ja, int Id);
}
