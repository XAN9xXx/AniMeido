using AniMeido.Contracts;
using AniMeido.Contracts.Models;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Text.Json;

namespace AniMeido.Plugin.Base.ViewModels
{
    public partial class CurrentSeasonViewModel : ObservableObject
    {
        [ObservableProperty]
        private ObservableCollection<Anime> _animeList = [];
        [ObservableProperty]
        private ObservableCollection<WeekdayGroup> _weekdayGroups = [];
        [ObservableProperty]
        private bool _isLoading = false;
        [ObservableProperty]
        string? _errorMessage = null;
        [ObservableProperty]
        private bool _hasData = false;
        [ObservableProperty]
        private bool _isError = false;
        IAnimeDataSource _animeDataSource;



        public CurrentSeasonViewModel(IAnimeDataSource dataSource)
        {
            _animeDataSource = dataSource;
        }

        /// <summary>
        /// 按星期分组展示当季番剧。
        /// </summary>
        private void RebuildGroups()
        {
            WeekdayGroups.Clear();

            var groups = AnimeList
                .GroupBy(a => a.Weekday)
                .OrderBy(g => g.Key ?? 99)
                .Select(g => new WeekdayGroup
                {
                    WeekdayName = g.Key switch
                    {
                        1 => "周一",
                        2 => "周二",
                        3 => "周三",
                        4 => "周四",
                        5 => "周五",
                        6 => "周六",
                        7 => "周日",
                        _ => "其他",
                    },
                    Items = new ObservableCollection<Anime>(g)
                });

            foreach (var group in groups)
            {
                WeekdayGroups.Add(group);
            }
        }

        [RelayCommand]
        private void RetryLoad()
        {
            LoadSeasonalAnimeCommand.Execute(null);
        }

        /// <summary>
        /// 加载当季番剧页面数据
        /// </summary>
        [RelayCommand]
        private async Task LoadSeasonalAnimeAsync(CancellationToken ct = default)
        {
            IsLoading = true;
            IsError = false;
            ErrorMessage = null;
            AnimeList.Clear();
            WeekdayGroups.Clear();
            HasData = false;
            var (year, season) = SeasonHelper.GetCurrentSeason();
            try
            {
                var list = await _animeDataSource.GetAnimeBySeasonAsync(
                    year,
                    season,
                    ct);
                AnimeList.Clear();
                foreach (var anime in list)
                    AnimeList.Add(anime);

                RebuildGroups();
                HasData = AnimeList.Count > 0;
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
    }
}
