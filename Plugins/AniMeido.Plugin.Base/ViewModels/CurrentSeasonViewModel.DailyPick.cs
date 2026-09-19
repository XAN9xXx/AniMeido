using System.ComponentModel;
using System.Globalization;
using AniMeido.Contracts.Models;
using AniMeido.Plugin.Base.Models;
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
        // 今日一抽只显示推荐页题材目录里的标签，不显示制作公司、季度等杂项。
        private static readonly HashSet<string> DailyPickTagCatalog = new(
            RecommendationTagCatalog.Tags,
            StringComparer.OrdinalIgnoreCase);

        private DateOnly _dailyPickDate;
        private CancellationTokenSource? _dailyPickDetailCts;
        // 已经请求过详情的那一部（成功或失败都算），回到页面时不重复请求。
        private CalendarEntry? _dailyPickDetailsFor;

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

        public bool HasDailyPickTags => DailyPickTags.Count > 0;

        /// <summary>写入中按钮变淡；按钮仍接住点击，不禁用，避免焦点被挤走。</summary>
        public double DailyPickActionOpacity => DailyPick?.IsSavingStatus == true
            ? SavingActionOpacity
            : 1;

        partial void OnDailyPickTagsChanged(IReadOnlyList<string> value)
            => OnPropertyChanged(nameof(HasDailyPickTags));

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
            else if (e.PropertyName == nameof(CalendarEntry.IsSavingStatus))
                OnPropertyChanged(nameof(DailyPickActionOpacity));
        }

        private void RaiseDailyPickStatusChanged()
        {
            OnPropertyChanged(nameof(DailyPickActionOpacity));
            OnPropertyChanged(nameof(IsDailyPickWatching));
            OnPropertyChanged(nameof(DailyPickWatchLabel));
            OnPropertyChanged(nameof(DailyPickFollowLabel));
        }

        /// <summary>换一个：沿当天的固定顺序往后找下一部还没有任何标记的作品。</summary>
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
        private async Task EnsureDailyPickAsync(int supplementaryGeneration)
        {
            bool IsCurrent() => !_supplementaryLoadsSuspended
                && supplementaryGeneration == _supplementaryLoadGeneration;
            if (!IsCurrent())
                return;

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
            if (!IsCurrent())
                return;

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

        /// <summary>候选：还没有任何标记的周更作品。</summary>
        private static bool IsDailyPickCandidate(CalendarEntry entry)
            => entry.Status == AnimeTrackingStatus.None;

        private void SetDailyPick(CalendarEntry? entry, DateOnly date)
        {
            _dailyPickDate = date;
            if (ReferenceEquals(DailyPick, entry))
                return;

            DailyPick = entry;
            if (!_supplementaryLoadsSuspended)
                _ = LoadDailyPickDetailsAsync(entry);
        }

        /// <summary>日历接口没有简介和标签，抽中后单独请求（有 7 天缓存）；失败时这两项留空。</summary>
        private async Task LoadDailyPickDetailsAsync(CalendarEntry? entry)
        {
            if (_supplementaryLoadsSuspended)
                return;

            CancelDailyPickDetails();
            _dailyPickDetailsFor = null;
            DailyPickDescription = "";
            DailyPickTags = [];
            if (entry is null)
                return;

            var cts = new CancellationTokenSource();
            _dailyPickDetailCts = cts;
            try
            {
                var detail = await _animeDataSource.GetAnimeDetailAsync(entry.Anime.ID, cts.Token);
                if (cts.IsCancellationRequested
                    || _supplementaryLoadsSuspended
                    || !ReferenceEquals(DailyPick, entry))
                {
                    return;
                }

                var tags = await _animeDataSource.GetTagsAsync(entry.Anime.ID, cts.Token);
                if (cts.IsCancellationRequested
                    || _supplementaryLoadsSuspended
                    || !ReferenceEquals(DailyPick, entry))
                    return;

                DailyPickDescription = detail?.Description?.Trim() ?? "";
                DailyPickTags = PickDailyTags(tags.Select(tag => tag.Name));
                _dailyPickDetailsFor = entry;
            }
            catch (OperationCanceledException) when (cts.IsCancellationRequested)
            {
            }
#pragma warning disable CA1031 // 简介和标签只是补充信息，任何失败都只留空，不打断页面
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[CurrentSeasonViewModel] LoadDailyPickDetailsAsync failed: {ex.Message}");
                // 失败也算请求过：回到页面时不自动重试。
                if (ReferenceEquals(_dailyPickDetailCts, cts))
                    _dailyPickDetailsFor = entry;
            }
#pragma warning restore CA1031
            finally
            {
                if (ReferenceEquals(_dailyPickDetailCts, cts))
                {
                    _dailyPickDetailCts = null;
                    cts.Dispose();
                }
            }
        }

        /// <summary>取消正在进行的详情请求；回到页面时重新请求。</summary>
        private void CancelDailyPickDetails()
        {
            if (_dailyPickDetailCts is not { } cts)
                return;

            _dailyPickDetailCts = null;
            cts.Cancel();
            cts.Dispose();
        }

        /// <summary>
        /// 从 Bangumi 标签里挑出最多三个：按返回顺序，只取题材目录里的，
        /// 去掉制作公司、季度等。一个都没有时返回空，标签行随之隐藏。
        /// </summary>
        internal static IReadOnlyList<string> PickDailyTags(IEnumerable<string?> names)
            => names
                .Select(name => name?.Trim())
                .OfType<string>()
                .Where(name => name.Length > 0 && DailyPickTagCatalog.Contains(name))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(3)
                .ToList();

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
            => StableShuffle.ByDate(animeIds, date);

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
    }
}
