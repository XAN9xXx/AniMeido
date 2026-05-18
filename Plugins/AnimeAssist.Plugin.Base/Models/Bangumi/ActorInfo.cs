namespace AniMeido.Plugin.Base.Models.Bangumi
{
    /// <summary>
    /// 声优信息。
    /// </summary>
    /// <remarks>
    /// 参数列表：int Id, string Name, PersonImages? Image
    /// </remarks>
    /// <param name="Id">声优的唯一标识符。</param>
    /// <param name="Name">声优的姓名。</param>
    /// <param name="Image">声优的头像图片信息。</param>
    internal record ActorInfo(int Id, string Name, PersonImages? Image);

}
