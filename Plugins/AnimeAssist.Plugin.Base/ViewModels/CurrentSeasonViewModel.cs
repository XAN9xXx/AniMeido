using AniMeido.Contracts;
using AniMeido.Contracts.Models;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

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
        IAnimeDataSource _animeDataSource;



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

        public CurrentSeasonViewModel(IAnimeDataSource dataSource)
        {
            _animeDataSource = dataSource;
        }

        [RelayCommand]
        private void RetryLoad()
        {
            LoadSeasonalAnimeCommand.Execute(null);
        }

        [RelayCommand]
        private async Task LoadSeasonalAnimeAsync()
        {
            IsLoading = true;
            var (year, season) = SeasonHelper.GetCurrentSeason();
            try
            {
                var list = await _animeDataSource.GetAnimeBySeasonAsync(
                    year,
                    season,
                    CancellationToken.None);
                AnimeList.Clear();
                foreach (var anime in list)
                    AnimeList.Add(anime);

                RebuildGroups();
            }
            catch (Exception ex)
            {
                ErrorMessage = $"Fail to load: {ex.Message}";
            }
            finally
            {
                IsLoading = false;
            }
        }
    }
}
