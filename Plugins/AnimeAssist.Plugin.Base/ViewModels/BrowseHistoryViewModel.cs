using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AniMeido.Contracts;
using AniMeido.Contracts.Models;
using AniMeido.Plugin.Base.Services;

namespace AniMeido.Plugin.Base.ViewModels
{
    public partial class BrowseHistoryViewModel : ObservableObject
    {
        private readonly BrowseHistoryService _browseHistory;
        private readonly IAnimeDataSource _dataSource;
        private readonly TrackingService _tracking;

        [ObservableProperty]
        private ObservableCollection<Anime> _historyList = [];

        [ObservableProperty]
        private bool _isLoading = false;

        [ObservableProperty]
        private bool _hasData = false;

        [ObservableProperty]
        private bool _isEmpty = true;

        public BrowseHistoryViewModel(
            BrowseHistoryService browseHistory,
            IAnimeDataSource dataSource,
            TrackingService tracking)
        {
            _browseHistory = browseHistory;
            _dataSource = dataSource;
            _tracking = tracking;
        }

        [RelayCommand]
        public async Task LoadHistoryAsync()
        {
            IsLoading = true;
            HasData = false;

            try
            {
                var records = await _browseHistory.GetHistoryAsync(50);
                var blocked = await _tracking.GetBlockedAnimeIdsAsync();

                // 先收集到临时列表，再一次性替换到可观测集合（保持同一个集合实例避免绑定断链）
                HistoryList.Clear();

                foreach (var (animeId, title, lastViewed, viewCount) in records)
                {
                    if (blocked.Contains(animeId))
                    {
                        continue;
                    }

                    // 优先从缓存/详情接口获取完整数据
                    Anime? anime = null;
                    try
                    {
                        anime = await _dataSource.GetAnimeDetailAsync(animeId, CancellationToken.None);
                    }
                    catch (HttpRequestException)
                    {
                        // 网络失败时使用快照标题
                    }
                    catch (TaskCanceledException)
                    {
                        // 请求取消时使用快照标题
                    }

                    if (anime == null)
                    {
                        // 构造一个最小对象用于显示
                        anime = new Anime(
                            animeId,
                            title ?? $"#{animeId}",
                            null, [], null, null, "", 0, 0
                        );
                    }

                    HistoryList.Add(anime);
                }

                HasData = HistoryList.Count > 0;
                IsEmpty = HistoryList.Count == 0;
            }
            finally
            {
                IsLoading = false;
            }
        }

        public async Task RemoveBlockedEntriesAsync()
        {
            var blocked = await _tracking.GetBlockedAnimeIdsAsync();
            for (var index = HistoryList.Count - 1; index >= 0; index--)
            {
                if (blocked.Contains(HistoryList[index].ID))
                {
                    HistoryList.RemoveAt(index);
                }
            }

            HasData = HistoryList.Count > 0;
            IsEmpty = HistoryList.Count == 0;
        }

        [RelayCommand]
        public async Task ClearHistoryAsync()
        {
            await _browseHistory.ClearAsync();
            HistoryList.Clear();
            HasData = false;
            IsEmpty = true;
        }
    }
}
