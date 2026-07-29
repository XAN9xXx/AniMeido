using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using AniMeido.Contracts.Models;

namespace AniMeido.Plugin.Base.ViewModels
{
    /// <summary>
    /// 描述关注管理中的一个状态分区。该模型只在 BasePlugin 内使用。
    /// </summary>
    public sealed partial class TrackingStatusSection(
        AnimeTrackingStatus status,
        string label,
        string glyph,
        string emptyMessage) : ObservableObject
    {
        public AnimeTrackingStatus Status { get; } = status;

        public string Label { get; } = label;

        public string Glyph { get; } = glyph;

        public string EmptyMessage { get; } = emptyMessage;

        public ObservableCollection<Anime> Items { get; } = [];

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(Header))]
        [NotifyPropertyChangedFor(nameof(HasItems))]
        private int _count;

        public string Header => $"{Label} ({Count})";

        public bool HasItems => Count > 0;

        public static IReadOnlyList<TrackingStatusSection> CreateDefaults() =>
        [
            new(AnimeTrackingStatus.Watching, "追番中", "\uE768", "暂无追番标记"),
            new(AnimeTrackingStatus.PlanToWatch, "补番中", "\uE916", "暂无补番标记"),
            new(AnimeTrackingStatus.NotInterested, "不感兴趣", "\uE711", "暂无不感兴趣标记"),
            new(AnimeTrackingStatus.Following, "关注", "\uEB51", "暂无关注标记"),
            new(AnimeTrackingStatus.Completed, "已看完", "\uE73E", "暂无已看完标记"),
            new(AnimeTrackingStatus.Dropped, "弃番", "\uE74D", "暂无弃番标记"),
            new(AnimeTrackingStatus.Blocked, "屏蔽", "\uE78B", "暂无屏蔽标记"),
        ];
    }
}
