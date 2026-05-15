namespace AnimeAssist.Contracts.Models
{
    /// <summary>
    /// 表示一个出版社/企划。
    /// </summary>
    /// <remarks>
    /// 参数列表：int ID, string Name, string? CoverURL
    /// </remarks>
    /// <param name="ID">出版社/企划的唯一标识符。</param>
    /// <param name="Name">出版社/企划的名称。</param>
    /// <param name="CoverURL">出版社/企划的封面图片URL。</param>
    public record Studio(int ID, string Name, string? CoverURL);
}
