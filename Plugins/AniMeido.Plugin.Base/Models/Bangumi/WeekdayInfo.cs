namespace AniMeido.Plugin.Base.Models.Bangumi
{
    /// <remarks>
    /// 包含英文、中文、日文名称和对应的ID（1-7，分别对应周一到周日）。
    /// </remarks>
    internal record WeekdayInfo(string? En, string? Cn, string? Ja, int Id);
}
