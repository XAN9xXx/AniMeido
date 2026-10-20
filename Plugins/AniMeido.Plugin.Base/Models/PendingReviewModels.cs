namespace AniMeido.Plugin.Base.Models;

/// <param name="RecordCounts">按表名统计的记录数，只含非零项。</param>
public sealed record PendingReviewItem(int AnimeId, int? LegacyBangumiId, string Title, string Reason,
    int? MergedInto, string? MergedIntoTitle, string? KeptAt, IReadOnlyDictionary<string, int> RecordCounts)
{
    // 面向用户的分类：多张表归成一类；浏览记录和隐藏推荐不算用户自己的记录，不列出。
    private static readonly (string Label, string[] Tables, string? Unit)[] RecordGroups =
    [
        ("追番状态", ["tracking", "tracking_events"], null),
        ("观看进度", ["anime_progress", "episode_progress", "watch_sessions", "manual_watch_events"], null),
        ("计划", ["anime_plans", "plan_reminders"], null),
        ("档案", ["anime_archives"], null),
        ("感想", ["archive_entries"], "条"),
        ("截图", ["screenshots"], "张"),
        ("个人标签", ["anime_personal_tags"], null),
    ];

    private string TargetTitle => MergedIntoTitle ?? "另一部作品";

    public string ExplanationText => Reason switch
    {
        "not_found" => "番剧资料里找不到这部作品，所以没有封面和简介。",
        "deactivated" => "这部作品已从番剧资料中移除，通常是重复或录错的条目。",
        "merged_conflict" => $"番剧资料把它和《{TargetTitle}》合并成了一部，两边都有你的记录。",
        _ => "在新的番剧资料里，它和你记录过的另一部作品是同一部，两边都有你的记录。",
    };

    public string RecordsText
    {
        get
        {
            var parts = RecordGroups
                .Select(g => (g.Label, g.Unit, Count: g.Tables.Sum(t => RecordCounts.GetValueOrDefault(t))))
                .Where(g => g.Count > 0)
                .Select(g => g.Unit is null ? g.Label : $"{g.Count} {g.Unit}{g.Label}")
                .ToList();
            return parts.Count == 0 ? "你只浏览过这部作品，没有其他记录。" : "你的记录：" + string.Join("、", parts);
        }
    }

    /// <summary>番剧资料已经指明要合并到哪部作品，可以跳过选择直接确认。</summary>
    public bool HasKnownTarget => Reason == "merged_conflict" && MergedInto is not null;

    /// <summary>迁移时撞上的那部作品没有登记，只能从用户自己记录过的作品里选。</summary>
    public bool PicksFromOwnRecords => Reason == "conflict";

    public string PrimaryActionText => HasKnownTarget ? $"合并到《{TargetTitle}》"
        : PicksFromOwnRecords ? "选择要合并到的作品…" : "选择对应的番剧…";

    public Uri? BangumiUri => LegacyBangumiId is { } id ? new Uri($"https://bgm.tv/subject/{id}") : null;
    public bool HasBangumiUri => LegacyBangumiId is not null;
}

public sealed record LocalAnimeTarget(int AnimeId, string Title)
{
    public override string ToString() => Title;
}

internal sealed record IdRemapEntry(long Seq, int From, int To, string At);
internal sealed record RemapTableChange(string Table, int Moved, int Discarded);
internal sealed record PersonalIdRemapResult(int From, int To, IReadOnlyList<RemapTableChange> Changes);
