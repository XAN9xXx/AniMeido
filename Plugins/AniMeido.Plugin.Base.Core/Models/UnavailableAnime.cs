using AniMeido.Contracts.Models;

namespace AniMeido.Plugin.Base.Models;

/// <summary>个人记录仍存在，但公共资料已停用或只剩本地标题快照。</summary>
internal sealed record UnavailableAnime : Anime
{
    internal UnavailableAnime(int id, string title)
        : base(id, title, null, [], null, null, string.Empty, 0, 0)
    {
    }
}
