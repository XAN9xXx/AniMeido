using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AniMeido.Contracts;
using AniMeido.Contracts.Models;
using AniMeido.Plugin.Base.Services;
using System.Text.Json;

namespace AniMeido.Plugin.Base.ViewModels
{
    public record TagItem(string TagName, bool IsExpanded);

    public partial class ManagementViewModel : ObservableObject
    {
        private readonly TrackingService _trackingService;
        private readonly IAnimeDataSource _animeDataSource;
        private readonly SavedTagService _savedTagService;


        [ObservableProperty]
        private ObservableCollection<Anime> _watchingList = [];
        [ObservableProperty]
        private ObservableCollection<Anime> _planToWatchList = [];
        [ObservableProperty]
        private ObservableCollection<Anime> _notInterestedList = [];
        [ObservableProperty]
        private ObservableCollection<Anime> _followingList = [];
        [ObservableProperty]
        private ObservableCollection<Anime> _completedList = [];
        [ObservableProperty]
        private ObservableCollection<Anime> _droppedList = [];
        [ObservableProperty]
        private ObservableCollection<Anime> _blockedList = [];
        [ObservableProperty]
        private bool _isLoading = false;
        [ObservableProperty]
        private bool _isError = false;
        [ObservableProperty]
        string? _errorMessage = null;
        [ObservableProperty]
        private int _watchingCount = 0;
        [ObservableProperty]
        private int _planToWatchCount = 0;
        [ObservableProperty]
        private int _notInterestedCount = 0;
        [ObservableProperty]
        private int _followingCount = 0;
        [ObservableProperty]
        private int _completedCount = 0;
        [ObservableProperty]
        private int _droppedCount = 0;
        [ObservableProperty]
        private int _blockedCount = 0;
        [ObservableProperty]
        private int _selectedTabIndex = 0;

        // Tag 管理
        [ObservableProperty]
        private ObservableCollection<TagItem> _tagList = [];
        [ObservableProperty]
        private ObservableCollection<Anime> _tagAnimeList = [];
        [ObservableProperty]
        private bool _isTagLoading = false;
        [ObservableProperty]
        private bool _hasTags = false;
        [ObservableProperty]
        private string _tagSearchText = "";


        public ManagementViewModel(TrackingService trackingService, IAnimeDataSource dataSource, SavedTagService savedTagService)
        {
            _trackingService = trackingService;
            _animeDataSource = dataSource;
            _savedTagService = savedTagService;
        }


        [RelayCommand]
        private async Task LoadDataAsync(CancellationToken ct = default)
        {
            IsLoading = true;
            IsError = false;
            ErrorMessage = null;

            try
            {
                // 获取各状态的番剧 ID 列表
                var watchingIds = await _trackingService.GetAnimeIdsByStatusAsync(AnimeTrackingStatus.Watching);
                var planIds = await _trackingService.GetAnimeIdsByStatusAsync(AnimeTrackingStatus.PlanToWatch);
                var notInterestedIds = await _trackingService.GetAnimeIdsByStatusAsync(AnimeTrackingStatus.NotInterested);
                var followingIds = await _trackingService.GetAnimeIdsByStatusAsync(AnimeTrackingStatus.Following);
                var completedIds = await _trackingService.GetAnimeIdsByStatusAsync(AnimeTrackingStatus.Completed);
                var droppedIds = await _trackingService.GetAnimeIdsByStatusAsync(AnimeTrackingStatus.Dropped);
                var blockedIds = await _trackingService.GetAnimeIdsByStatusAsync(AnimeTrackingStatus.Blocked);

                // 先设置真实计数（来自数据库），再并发加载详情
                WatchingCount = watchingIds.Count;
                PlanToWatchCount = planIds.Count;
                NotInterestedCount = notInterestedIds.Count;
                FollowingCount = followingIds.Count;
                CompletedCount = completedIds.Count;
                DroppedCount = droppedIds.Count;
                BlockedCount = blockedIds.Count;

                // 并发加载番剧详情（限流，最多 4 个并发请求）
                WatchingList.Clear();
                foreach (var anime in await LoadAnimeDetailsConcurrentAsync(watchingIds, ct))
                    WatchingList.Add(anime);

                PlanToWatchList.Clear();
                foreach (var anime in await LoadAnimeDetailsConcurrentAsync(planIds, cancellationToken: default))
                    PlanToWatchList.Add(anime);

                NotInterestedList.Clear();
                foreach (var anime in await LoadAnimeDetailsConcurrentAsync(notInterestedIds, cancellationToken: default))
                    NotInterestedList.Add(anime);

                FollowingList.Clear();
                foreach (var anime in await LoadAnimeDetailsConcurrentAsync(followingIds, cancellationToken: default))
                    FollowingList.Add(anime);

                CompletedList.Clear();
                foreach (var anime in await LoadAnimeDetailsConcurrentAsync(completedIds, cancellationToken: default))
                    CompletedList.Add(anime);

                DroppedList.Clear();
                foreach (var anime in await LoadAnimeDetailsConcurrentAsync(droppedIds, cancellationToken: default))
                    DroppedList.Add(anime);

                BlockedList.Clear();
                foreach (var anime in await LoadAnimeDetailsConcurrentAsync(blockedIds, cancellationToken: default))
                    BlockedList.Add(anime);
            }
            catch (HttpRequestException ex)
            {
                ErrorMessage = $"网络请求失败：{ex.Message}";
                IsError = true;
            }
            catch (TaskCanceledException)
            {
                // 取消是预期行为（被新请求替代），不当作错误处理
                return;
            }
            catch (Exception ex) when (ex is InvalidOperationException or JsonException)
            {
                ErrorMessage = $"数据解析失败：{ex.Message}";
                IsError = true;
            }
            finally
            {
                IsLoading = false;
            }
        }


        [RelayCommand]
        private async Task RemoveFromWatchingAsync(int animeId)
        {
            await _trackingService.RemoveStatusAsync(animeId);
            var item = WatchingList.FirstOrDefault(a => a.ID == animeId);
            if (item != null) WatchingList.Remove(item);
            WatchingCount = WatchingList.Count;
        }

        [RelayCommand]
        private async Task RemoveFromPlanAsync(int animeId)
        {
            await _trackingService.RemoveStatusAsync(animeId);
            var item = PlanToWatchList.FirstOrDefault(a => a.ID == animeId);
            if (item != null) PlanToWatchList.Remove(item);
            PlanToWatchCount = PlanToWatchList.Count;
        }

        [RelayCommand]
        private async Task RemoveFromNotInterestedAsync(int animeId)
        {
            await _trackingService.RemoveStatusAsync(animeId);
            var item = NotInterestedList.FirstOrDefault(a => a.ID == animeId);
            if (item != null) NotInterestedList.Remove(item);
            NotInterestedCount = NotInterestedList.Count;
        }

        [RelayCommand]
        private async Task RemoveFromFollowingAsync(int animeId)
        {
            await _trackingService.RemoveStatusAsync(animeId);
            var item = FollowingList.FirstOrDefault(a => a.ID == animeId);
            if (item != null) FollowingList.Remove(item);
            FollowingCount = FollowingList.Count;
        }

        [RelayCommand]
        private async Task RemoveFromCompletedAsync(int animeId)
        {
            await _trackingService.RemoveStatusAsync(animeId);
            var item = CompletedList.FirstOrDefault(a => a.ID == animeId);
            if (item != null) CompletedList.Remove(item);
            CompletedCount = CompletedList.Count;
        }

        [RelayCommand]
        private async Task RemoveFromDroppedAsync(int animeId)
        {
            await _trackingService.RemoveStatusAsync(animeId);
            var item = DroppedList.FirstOrDefault(a => a.ID == animeId);
            if (item != null) DroppedList.Remove(item);
            DroppedCount = DroppedList.Count;
        }

        [RelayCommand]
        private async Task RemoveFromBlockedAsync(int animeId)
        {
            await _trackingService.RemoveStatusAsync(animeId);
            var item = BlockedList.FirstOrDefault(a => a.ID == animeId);
            if (item != null) BlockedList.Remove(item);
            BlockedCount = BlockedList.Count;
        }

        // ======== Tag 管理 ========

        [RelayCommand]
        private async Task LoadTagsAsync()
        {
            IsTagLoading = true;
            try
            {
                var tags = await _savedTagService.GetAllSavedTagsAsync();
                TagList.Clear();
                foreach (var name in tags)
                {
                    TagList.Add(new TagItem(name, false));
                }
                HasTags = TagList.Count > 0;
                TagAnimeList.Clear();
            }
            finally
            {
                IsTagLoading = false;
            }
        }

        [RelayCommand]
        private async Task ToggleTagAsync(TagItem tag)
        {
            // 先关闭其他展开的 tag
            for (int i = TagList.Count - 1; i >= 0; i--)
            {
                var t = TagList[i];
                if (t != tag && t.IsExpanded)
                {
                    TagList[i] = t with { IsExpanded = false };
                }
            }

            var tagName = tag.TagName;
            var index = -1;
            for (int i = TagList.Count - 1; i >= 0; i--)
            {
                if (TagList[i].TagName == tagName)
                {
                    index = i;
                    break;
                }
            }
            if (index < 0) return;

            var current = TagList[index];
            var expanded = !current.IsExpanded;
            TagList[index] = current with { IsExpanded = expanded };

            if (expanded)
            {
                IsTagLoading = true;
                try
                {
                    TagAnimeList.Clear();
                    // 通过 Bangumi API 搜索带此 Tag 的番剧
                    var (results, _) = await _animeDataSource.SearchByTagAsync(
                        tagName, 0, "rank", CancellationToken.None);
                    foreach (var anime in results.Take(20))
                    {
                        TagAnimeList.Add(anime);
                    }
                }
                finally
                {
                    IsTagLoading = false;
                }
            }
            else
            {
                TagAnimeList.Clear();
            }
        }

        [RelayCommand]
        private async Task DeleteTagAsync(TagItem tag)
        {
            var tagName = tag.TagName;

            // 先在 UI 上移除（避免异步等待期间用户重复操作）
            for (int i = TagList.Count - 1; i >= 0; i--)
            {
                if (TagList[i].TagName == tagName)
                {
                    TagList.RemoveAt(i);
                    break;
                }
            }

            TagAnimeList.Clear();
            HasTags = TagList.Count > 0;
            await _savedTagService.RemoveTagAsync(tagName);
        }

        /// <summary>
        /// 并发加载番剧详情，限制最大并发数 4。
        /// 使用 Parallel.ForEachAsync 避免大数据量时创建过多 Task 对象。
        /// </summary>
        private async Task<List<Anime>> LoadAnimeDetailsConcurrentAsync(IReadOnlyList<int> ids, CancellationToken cancellationToken)
        {
            var results = new ConcurrentBag<Anime>();
            var parallelOptions = new ParallelOptions
            {
                MaxDegreeOfParallelism = 4,
                CancellationToken = cancellationToken
            };

            await Parallel.ForEachAsync(ids, parallelOptions, async (id, token) =>
            {
                try
                {
                    token.ThrowIfCancellationRequested();
                    var anime = await _animeDataSource.GetAnimeDetailAsync(id, token);
                    if (anime != null)
                        results.Add(anime);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (HttpRequestException)
                {
                    // 单个作品获取失败时跳过
                }
                catch (Exception ex) when (ex is InvalidOperationException or JsonException)
                {
                    // 单个作品解析失败时跳过
                }
            });

            return results.ToList();
        }
    }
}
