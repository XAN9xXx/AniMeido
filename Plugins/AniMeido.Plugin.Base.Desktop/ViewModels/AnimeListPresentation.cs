using System.Collections.ObjectModel;
using AniMeido.Contracts.Models;

namespace AniMeido.Plugin.Base.ViewModels
{
    /// <summary>
    /// 统一番剧列表的可见性、标题过滤和星期分组规则。
    /// </summary>
    public static class AnimeListPresentation
    {
        public static IReadOnlyList<Anime> Filter(
            IEnumerable<Anime> source,
            IReadOnlySet<int>? blockedIds = null,
            string? titleQuery = null,
            bool includeBlocked = false)
        {
            var query = titleQuery?.Trim();
            return source
                .Where(anime =>
                    includeBlocked ||
                    blockedIds is null ||
                    !blockedIds.Contains(anime.ID))
                .Where(anime =>
                    string.IsNullOrWhiteSpace(query) ||
                    anime.Title.Contains(
                        query,
                        StringComparison.OrdinalIgnoreCase))
                .ToList();
        }

        public static IReadOnlyList<WeekdayGroup> GroupByWeekday(
            IEnumerable<Anime> source) =>
            source
                .GroupBy(anime => anime.Weekday)
                .OrderBy(group => group.Key ?? 99)
                .Select(group => new WeekdayGroup
                {
                    Weekday = group.Key,
                    WeekdayName = GetWeekdayName(group.Key),
                    Items = new ObservableCollection<Anime>(group),
                })
                .ToList();

        public static string GetWeekdayName(int? weekday) => weekday switch
        {
            1 => "周一",
            2 => "周二",
            3 => "周三",
            4 => "周四",
            5 => "周五",
            6 => "周六",
            7 => "周日",
            _ => "其他",
        };

        public static int ToBangumiWeekday(DayOfWeek day) =>
            day == DayOfWeek.Sunday ? 7 : (int)day;
    }
}
