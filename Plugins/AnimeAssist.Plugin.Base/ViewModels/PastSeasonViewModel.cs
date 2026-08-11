using AniMeido.Contracts;
using AniMeido.Contracts.Models;
using AniMeido.Plugin.Base.Exceptions;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Text.Json;
using System.Collections.ObjectModel;

namespace AniMeido.Plugin.Base.ViewModels
{
    public partial class PastSeasonViewModel : ObservableObject
    {
        [ObservableProperty]
        private ObservableCollection<Anime> _animeList = [];
        [ObservableProperty]
        private bool _isLoading = false;
        [ObservableProperty]
        string? _errorMessage = null;
        [ObservableProperty]
        private bool _hasData = false;
        [ObservableProperty]
        private bool _isError = false;
        [ObservableProperty]
        private int _totalCount = 0;
        public IReadOnlyList<Anime> LoadedAnime { get; private set; } = [];
        IAnimeDataSource _animeDataSource;
        int _lastYear;
        Season _lastSeason;



        public PastSeasonViewModel(IAnimeDataSource dataSource)
        {
            _animeDataSource = dataSource;
        }



        /// <summary>
        /// 加载往季番剧页面数据
        /// </summary>
        /// <param name="year">要加载的年份</param>
        /// <param name="season">要加载的季度</param>
        public async Task LoadPastSeasonAnimeAsync(int year, Season season, CancellationToken ct = default)
        {
            _lastYear = year;
            _lastSeason = season;
            IsLoading = true;
            IsError = false;
            ErrorMessage = null;
            AnimeList.Clear();
            LoadedAnime = [];
            TotalCount = 0;
            HasData = false;

            try
            {
                var list = await _animeDataSource.GetAnimeBySeasonAsync(
                    year,
                    season,
                    ct);
                LoadedAnime = list.ToArray();
                // 一次性替换集合引用（单次 PropertyChanged），避免 N 次 Add 触发 N 次布局
                AnimeList = new ObservableCollection<Anime>(LoadedAnime);

                // 更新统计信息
                TotalCount = list.Count;
                HasData = list.Count > 0;
            }
            catch (HttpRequestException ex)
            {
                ErrorMessage = $"网络请求失败：{ex.Message}";
                HasData = false;
                IsError = true;
            }
            catch (BangumiApiException ex)
            {
                ErrorMessage = $"数据源请求失败：{ex.Message}";
                HasData = false;
                IsError = true;
            }
            catch (TaskCanceledException) when (ct.IsCancellationRequested)
            {
                // 用户切换年份/季度引起的取消，静默忽略
                return;
            }
            catch (TaskCanceledException)
            {
                // HTTP 超时或其他网络层取消，作为错误处理
                ErrorMessage = "网络请求超时，请检查网络后重试";
                HasData = false;
                IsError = true;
            }
            catch (Exception ex) when (ex is InvalidOperationException or JsonException)
            {
                ErrorMessage = $"数据解析失败：{ex.Message}";
                HasData = false;
                IsError = true;
            }
            finally
            {
                // 取消的请求不修改 IsLoading，新请求已设置自己的状态
                if (!ct.IsCancellationRequested)
                    IsLoading = false;
            }
        }

        // 点击重试时尝试加载相同时段的数据
        [RelayCommand]
        private void RetryLoad()
        {
            _ = LoadPastSeasonAnimeAsync(_lastYear, _lastSeason);
        }
    }
}
