using AniMeido.Contracts;
using AniMeido.Contracts.Models;
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
            TotalCount = 0;
            HasData = false;

            try
            {
                var list = await _animeDataSource.GetAnimeBySeasonAsync(
                    year,
                    season,
                    ct);
                AnimeList.Clear();
                foreach (var anime in list)
                {
                    AnimeList.Add(anime);
                }

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
            catch (TaskCanceledException)
            {
                // 取消是预期行为（被新请求替代），不当作错误处理
                return;
            }
            catch (Exception ex) when (ex is InvalidOperationException or JsonException)
            {
                ErrorMessage = $"数据解析失败：{ex.Message}";
                HasData = false;
                IsError = true;
            }
            finally
            {
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
