using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AniMeido.Contracts;
using AniMeido.Contracts.Models;
using AniMeido.Plugin.Base.Services;

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
        private async Task LoadDataAsync()
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

                // 逐个加载番剧详情（最多显示 20 条）
                WatchingList.Clear();
                foreach (var id in watchingIds.Take(20))
                {
                    var anime = await _animeDataSource.GetAnimeDetailAsync(id, CancellationToken.None);
                    if (anime != null) WatchingList.Add(anime);
                }

                PlanToWatchList.Clear();
                foreach (var id in planIds.Take(20))
                {
                    var anime = await _animeDataSource.GetAnimeDetailAsync(id, CancellationToken.None);
                    if (anime != null) PlanToWatchList.Add(anime);
                }

                NotInterestedList.Clear();
                foreach (var id in notInterestedIds.Take(20))
                {
                    var anime = await _animeDataSource.GetAnimeDetailAsync(id, CancellationToken.None);
                    if (anime != null) NotInterestedList.Add(anime);
                }

                FollowingList.Clear();
                foreach (var id in followingIds.Take(20))
                {
                    var anime = await _animeDataSource.GetAnimeDetailAsync(id, CancellationToken.None);
                    if (anime != null) FollowingList.Add(anime);
                }

                CompletedList.Clear();
                foreach (var id in completedIds.Take(20))
                {
                    var anime = await _animeDataSource.GetAnimeDetailAsync(id, CancellationToken.None);
                    if (anime != null) CompletedList.Add(anime);
                }

                DroppedList.Clear();
                foreach (var id in droppedIds.Take(20))
                {
                    var anime = await _animeDataSource.GetAnimeDetailAsync(id, CancellationToken.None);
                    if (anime != null) DroppedList.Add(anime);
                }

                BlockedList.Clear();
                foreach (var id in blockedIds.Take(20))
                {
                    var anime = await _animeDataSource.GetAnimeDetailAsync(id, CancellationToken.None);
                    if (anime != null) BlockedList.Add(anime);
                }

                WatchingCount = WatchingList.Count;
                PlanToWatchCount = PlanToWatchList.Count;
                NotInterestedCount = NotInterestedList.Count;
                FollowingCount = FollowingList.Count;
                CompletedCount = CompletedList.Count;
                DroppedCount = DroppedList.Count;
                BlockedCount = BlockedList.Count;
            }
            catch (Exception ex)
            {
                ErrorMessage = $"加载失败：{ex.Message}";
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
    }
}
