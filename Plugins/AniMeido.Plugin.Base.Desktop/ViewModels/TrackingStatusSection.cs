using CommunityToolkit.Mvvm.ComponentModel;
using AniMeido.Contracts.Models;

namespace AniMeido.Plugin.Base.ViewModels
{
    /// <summary>
    /// 描述“我的番剧”中的一个状态分区。该模型只在 BasePlugin 内使用。
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

        /// <summary>左侧导航中当前选中的状态。</summary>
        [ObservableProperty]
        private bool _isSelected;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(Header))]
        [NotifyPropertyChangedFor(nameof(HasItems))]
        private int _count;

        public string Header => $"{Label} ({Count})";

        public bool HasItems => Count > 0;

        public static IReadOnlyList<TrackingStatusSection> CreateDefaults() =>
        [
            // 常用的五个在前平铺，“不感兴趣”“屏蔽”在后，页面上折叠为“不想看到”。
            new(AnimeTrackingStatus.Watching, "追番中", "\uE768", "暂无追番标记"),
            new(AnimeTrackingStatus.PlanToWatch, "补番中", "\uE916", "暂无补番标记"),
            new(AnimeTrackingStatus.Following, "关注", "\uEB51", "暂无关注标记"),
            new(AnimeTrackingStatus.Completed, "已看完", "\uE73E", "暂无已看完标记"),
            new(AnimeTrackingStatus.Dropped, "弃番", "\uE74D", "暂无弃番标记"),
            new(AnimeTrackingStatus.NotInterested, "不感兴趣", "\uE711", "暂无不感兴趣标记"),
            new(AnimeTrackingStatus.Blocked, "屏蔽", "\uE78B", "暂无屏蔽标记"),
        ];
    }
}
