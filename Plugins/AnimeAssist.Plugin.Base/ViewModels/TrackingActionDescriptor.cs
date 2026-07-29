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
        bool currentSeasonOnly = false,
        bool oldSeasonOnly = false) : ObservableObject
    {
        public AnimeTrackingStatus Status { get; } = status;

        public string Label { get; } = label;

        public string ActiveLabel { get; } = activeLabel;

        public string Glyph { get; } = glyph;

        public bool CurrentSeasonOnly { get; } = currentSeasonOnly;

        public bool OldSeasonOnly { get; } = oldSeasonOnly;

        [ObservableProperty]
        private bool _isSelected;

        [ObservableProperty]
        private bool _isVisible = !(currentSeasonOnly || oldSeasonOnly);

        public void UpdateAvailability(bool isCurrentSeason, bool isOldSeason)
        {
            IsVisible = (!CurrentSeasonOnly || isCurrentSeason) &&
                (!OldSeasonOnly || isOldSeason);
        }

        public static IReadOnlyList<TrackingActionDescriptor> CreateDefaults() =>
        [
            new(
                AnimeTrackingStatus.Watching,
                "追番",
                "追番中",
                "\uE8FB",
                currentSeasonOnly: true),
            new(
                AnimeTrackingStatus.PlanToWatch,
                "补番",
                "补番中",
                "\uE1D4",
                oldSeasonOnly: true),
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
