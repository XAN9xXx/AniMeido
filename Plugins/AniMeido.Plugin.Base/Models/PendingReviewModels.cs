namespace AniMeido.Plugin.Base.Models;

public sealed record PendingReviewItem(int AnimeId, int? LegacyBangumiId, string Title, string Reason,
    int? MergedInto, string? KeptAt, IReadOnlyDictionary<string, int> RecordCounts)
{
    public string IdentityText => LegacyBangumiId is { } id ? $"原 Bangumi 条目 #{id}" : $"公共 ID {AnimeId}";
    public string ReasonText => Reason switch
    {
        "not_found" => "资料服务未收录",
        "deactivated" => "已停用",
        "merged_conflict" => "已被合并，目标已有记录",
        _ => "与已有记录冲突",
    };
    public string CountsText => string.Join(" · ", RecordCounts.Select(p => $"{p.Key} {p.Value}"));
}

public sealed record LocalAnimeTarget(int AnimeId, string Title)
{
    public override string ToString() => $"{Title}（{AnimeId}）";
}

internal sealed record IdRemapEntry(long Seq, int From, int To, string At);
internal sealed record RemapTableChange(string Table, int Moved, int Discarded);
internal sealed record PersonalIdRemapResult(int From, int To, IReadOnlyList<RemapTableChange> Changes);
