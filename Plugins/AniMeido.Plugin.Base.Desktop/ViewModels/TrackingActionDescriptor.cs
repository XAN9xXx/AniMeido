using AniMeido.Contracts.Models;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AniMeido.Plugin.Base.ViewModels
{
    /// <summary>
    /// 描述详情页中的一个关注状态操作。
    /// </summary>
    public sealed partial class TrackingActionDescriptor(
        AnimeTrackingStatus status,
        string label,
        string activeLabel,
        string glyph,
        bool ongoingOnly = false,
        bool catchUpOnly = false) : ObservableObject
    {
        public AnimeTrackingStatus Status { get; } = status;

        public string Label { get; } = label;

        public string ActiveLabel { get; } = activeLabel;

        public string Glyph { get; } = glyph;

        /// <summary>
        /// 仅当作品尚未完结时可用，即本季在播或尚未开播。
        /// 不以“本季”为判据：未上映的作品同样属于可追范围。
        /// </summary>
        public bool OngoingOnly { get; } = ongoingOnly;

        /// <summary>仅当作品属于往季时可用。</summary>
        public bool CatchUpOnly { get; } = catchUpOnly;

        [ObservableProperty]
        private bool _isSelected;

        [ObservableProperty]
        private bool _isVisible = !(ongoingOnly || catchUpOnly);

        public void UpdateAvailability(bool allowsOngoing, bool allowsCatchUp)
        {
            IsVisible = (!OngoingOnly || allowsOngoing) &&
                (!CatchUpOnly || allowsCatchUp);
        }

        public static IReadOnlyList<TrackingActionDescriptor> CreateDefaults() =>
        [
            new(
                AnimeTrackingStatus.Watching,
                "追番",
                "追番中",
                "\uE8FB",
                ongoingOnly: true),
            new(
                AnimeTrackingStatus.PlanToWatch,
                "补番",
                "补番中",
                "\uE1D4",
                catchUpOnly: true),
            new(
                AnimeTrackingStatus.NotInterested,
                "不感兴趣",
                "不感兴趣",
                "\uE711"),
            new(
                AnimeTrackingStatus.Following,
                "关注",
                "关注中",
                "\uE1CE"),
            new(
                AnimeTrackingStatus.Completed,
                "已看完",
                "已看完",
                "\uE930"),
            new(
                AnimeTrackingStatus.Dropped,
                "弃番",
                "已弃番",
                "\uE74C"),
            new(
                AnimeTrackingStatus.Blocked,
                "屏蔽",
                "已屏蔽",
                "\uE76C"),
        ];
    }
}
