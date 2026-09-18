using System.ComponentModel;
using System.Globalization;
using AniMeido.Contracts.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AniMeido.Plugin.Base.ViewModels
{
    /// <summary>
    /// 放送日历下方的“今日一抽”：每天从周更作品里按固定顺序抽一部。
    /// 抽到的作品按日期存进本地，同一天重启仍是它；“换一个”沿同一顺序往后找。
    /// </summary>
    public partial class CurrentSeasonViewModel
    {
        private DateOnly _dailyPickDate;
        private CancellationTokenSource? _dailyPickDetailCts;

        [ObservableProperty]
        private CalendarEntry? _dailyPick;

        [ObservableProperty]
        private string _dailyPickDescription = "";

        [ObservableProperty]
        private IReadOnlyList<string> _dailyPickTags = [];

        public bool HasDailyPick => DailyPick is not null;

        public string DailyPickMeta => DailyPick is null ? "" : BuildDailyPickMeta(DailyPick.Anime);

        public string DailyPickScoreText => DailyPick?.Anime.Score is { } score
            ? score.ToString("F1", CultureInfo.InvariantCulture)
            : "";

        public bool HasDailyPickScore => DailyPickScoreText.Length > 0;

        public bool IsDailyPickWatching => DailyPick?.Status == AnimeTrackingStatus.Watching;

        public string DailyPickWatchLabel => IsDailyPickWatching ? "追番中 ✓" : "追番";

        public string DailyPickFollowLabel =>
            DailyPick?.Status == AnimeTrackingStatus.Following ? "关注中 ✓" : "关注";

        public bool HasDailyPickDescription => DailyPickDescription.Length > 0;

        partial void OnDailyPickChanged(CalendarEntry? oldValue, CalendarEntry? newValue)
        {
            if (oldValue is not null)
                oldValue.PropertyChanged -= OnDailyPickEntryChanged;
            if (newValue is not null)
                newValue.PropertyChanged += OnDailyPickEntryChanged;

            OnPropertyChanged(nameof(HasDailyPick));
            OnPropertyChanged(nameof(DailyPickMeta));
            OnPropertyChanged(nameof(DailyPickScoreText));
            OnPropertyChanged(nameof(HasDailyPickScore));
            RaiseDailyPickStatusChanged();
        }

        partial void OnDailyPickDescriptionChanged(string value)
            => OnPropertyChanged(nameof(HasDailyPickDescription));

        private void OnDailyPickEntryChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(CalendarEntry.Status))
                RaiseDailyPickStatusChanged();
        }

        private void RaiseDailyPickStatusChanged()
        {
            OnPropertyChanged(nameof(IsDailyPickWatching));
            OnPropertyChanged(nameof(DailyPickWatchLabel));
            OnPropertyChanged(nameof(DailyPickFollowLabel));
        }

        /// <summary>换一个：沿当天的固定顺序往后找下一部未标记、且不在本季发现里的作品。</summary>
        [RelayCommand]
        private async Task NextDailyPickAsync()
        {
            var today = DateOnly.FromDateTime(DateTime.Today);
            var weekly = WeeklyEntriesById();
            var next = FindNextDailyPick(
                BuildDailyOrder(weekly.Keys, today),
                DailyPick?.Anime.ID,
                id => IsDailyPickCandidate(weekly[id]));
            if (next is not { } id)
                return;

            SetDailyPick(weekly[id], today);
            await TrySaveDailyPickAsync(today, id);
        }

        /// <summary>
        /// 加载或重新读取标记后调用：当天已有抽到的作品就沿用；
        /// 没有、跨了天、或那部作品已不在列表里（例如刚被屏蔽）时，重新抽。
        /// </summary>
        private async Task EnsureDailyPickAsync()
        {
            var today = DateOnly.FromDateTime(DateTime.Today);
            var weekly = WeeklyEntriesById();
            if (DailyPick is { } current
                && _dailyPickDate == today
                && weekly.TryGetValue(current.Anime.ID, out var same)
                && ReferenceEquals(same, current))
            {
                return;
            }

            var stored = await TryLoadDailyPickStateAsync();
            if (stored is { } state
                && state.Date == today
                && weekly.TryGetValue(state.AnimeId, out var storedEntry))
            {
                SetDailyPick(storedEntry, today);
                return;
            }

            // 从记录的那部之后接着找，避免被屏蔽后又回到当天第一部。
            var startAfter = stored is { } previous && previous.Date == today
                ? previous.AnimeId
                : (int?)null;
            var next = FindNextDailyPick(
                BuildDailyOrder(weekly.Keys, today),
                startAfter,
                id => IsDailyPickCandidate(weekly[id]));
            SetDailyPick(next is { } id ? weekly[id] : null, today);
            if (next is { } saved)
                await TrySaveDailyPickAsync(today, saved);
        }

        private Dictionary<int, CalendarEntry> WeeklyEntriesById()
            => _entries
                .Where(entry => !entry.IsOther)
                .ToDictionary(entry => entry.Anime.ID);

        /// <summary>候选：还没有任何标记、此刻不在本季发现里的周更作品。</summary>
        private bool IsDailyPickCandidate(CalendarEntry entry)
            => entry.Status == AnimeTrackingStatus.None
                && DiscoverPicks.All(pick => pick.Entry.Anime.ID != entry.Anime.ID);

        private void SetDailyPick(CalendarEntry? entry, DateOnly date)
        {
            _dailyPickDate = date;
            if (ReferenceEquals(DailyPick, entry))
                return;

            DailyPick = entry;
            // 本季发现不重复显示抽到的作品。
            RefreshDiscover();
            _ = LoadDailyPickDetailsAsync(entry);
        }

        /// <summary>日历接口没有简介和标签，抽中后单独请求（有 7 天缓存）；失败时这两项留空。</summary>
        private async Task LoadDailyPickDetailsAsync(CalendarEntry? entry)
        {
            _dailyPickDetailCts?.Cancel();
            _dailyPickDetailCts?.Dispose();
            _dailyPickDetailCts = null;
            DailyPickDescription = "";
            DailyPickTags = [];
            if (entry is null)
                return;

            var cts = new CancellationTokenSource();
            _dailyPickDetailCts = cts;
            try
            {
                var detail = await _animeDataSource.GetAnimeDetailAsync(entry.Anime.ID, cts.Token);
                var tags = await _animeDataSource.GetTagsAsync(entry.Anime.ID, cts.Token);
                if (cts.IsCancellationRequested || !ReferenceEquals(DailyPick, entry))
                    return;

                DailyPickDescription = detail?.Description?.Trim() ?? "";
                DailyPickTags = tags
                    .Select(tag => tag.Name)
                    .Where(name => !string.IsNullOrWhiteSpace(name))
                    .Take(3)
                    .ToList();
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
            }
#pragma warning disable CA1031 // 简介和标签只是补充信息，任何失败都只留空，不打断页面
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[CurrentSeasonViewModel] LoadDailyPickDetailsAsync failed: {ex.Message}");
            }
#pragma warning restore CA1031
        }

        private async Task<(DateOnly Date, int AnimeId)?> TryLoadDailyPickStateAsync()
        {
            try
            {
                return await _tracking.LoadCalendarDailyPickAsync();
            }
            catch (Microsoft.Data.Sqlite.SqliteException ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[CurrentSeasonViewModel] LoadCalendarDailyPickAsync failed: {ex.Message}");
                return null;
            }
        }

        private async Task TrySaveDailyPickAsync(DateOnly date, int animeId)
        {
            try
            {
                await _tracking.SaveCalendarDailyPickAsync(date, animeId);
            }
            catch (Microsoft.Data.Sqlite.SqliteException ex)
            {
                // 保存失败只影响重启后的结果，本次运行照常显示。
                System.Diagnostics.Debug.WriteLine(
                    $"[CurrentSeasonViewModel] SaveCalendarDailyPickAsync failed: {ex.Message}");
            }
        }

        private static string BuildDailyPickMeta(Anime anime)
        {
            var parts = new List<string> { AnimeListPresentation.GetWeekdayName(anime.Weekday) };
            if (anime.MediaFormat != AnimeMediaFormat.Unknown)
                parts.Add(AnimeReleaseClassifier.GetMediaFormatText(anime.MediaFormat));
            if (anime.AirDate is { } airDate)
                parts.Add($"{airDate.Month} 月 {airDate.Day} 日开播");
            return string.Join(" · ", parts);
        }

        /// <summary>
        /// 当天固定的抽取顺序：按作品 ID 与日期混合出的键排序。
        /// 不用 Random 或字符串哈希，它们在不同运行之间不保证一致，重启后会对不上。
        /// </summary>
        internal static IReadOnlyList<int> BuildDailyOrder(IEnumerable<int> animeIds, DateOnly date)
            => animeIds
                .Distinct()
                .OrderBy(id => Mix(((ulong)(uint)date.DayNumber << 32) | (uint)id))
                .ThenBy(id => id)
                .ToList();

        /// <summary>从 <paramref name="currentId"/> 之后开始找第一部符合条件的作品，到末尾回到开头；找不到返回 null。</summary>
        internal static int? FindNextDailyPick(
            IReadOnlyList<int> order,
            int? currentId,
            Func<int, bool> isCandidate)
        {
            if (order.Count == 0)
                return null;

            var start = currentId is { } id ? IndexOf(order, id) + 1 : 0;
            for (var step = 0; step < order.Count; step++)
            {
                var candidate = order[(start + step) % order.Count];
                if (candidate != currentId && isCandidate(candidate))
                    return candidate;
            }

            return null;
        }

        private static int IndexOf(IReadOnlyList<int> order, int id)
        {
            for (var index = 0; index < order.Count; index++)
            {
                if (order[index] == id)
                    return index;
            }

            return -1;
        }

        // SplitMix64 的混合步骤：输入相同则结果相同，且分布足够打散。
        private static ulong Mix(ulong value)
        {
            unchecked
            {
                value += 0x9E3779B97F4A7C15UL;
                value = (value ^ (value >> 30)) * 0xBF58476D1CE4E5B9UL;
                value = (value ^ (value >> 27)) * 0x94D049BB133111EBUL;
                return value ^ (value >> 31);
            }
        }
    }
}
