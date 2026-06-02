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

        [ObservableProperty]
        private ObservableCollection<Anime> _historyList = [];

        [ObservableProperty]
        private bool _isLoading = false;

        [ObservableProperty]
        private bool _hasData = false;

        [ObservableProperty]
        private bool _isEmpty = true;

        public BrowseHistoryViewModel(BrowseHistoryService browseHistory, IAnimeDataSource dataSource)
        {
            _browseHistory = browseHistory;
            _dataSource = dataSource;
        }

        [RelayCommand]
        public async Task LoadHistoryAsync()
        {
            IsLoading = true;
            HasData = false;

            try
            {
                var records = await _browseHistory.GetHistoryAsync(50);
                HistoryList.Clear();

                foreach (var (animeId, title, lastViewed, viewCount) in records)
                {
                    // 优先从缓存/详情接口获取完整数据
                    Anime? anime = null;
                    try
                    {
                        anime = await _dataSource.GetAnimeDetailAsync(animeId, CancellationToken.None);
                    }
                    catch
                    {
                        // 网络失败时使用快照标题
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
