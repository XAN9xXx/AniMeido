using AniMeido.Contracts;
using AniMeido.Contracts.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Text;
using System.Threading.Tasks;

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

        public async Task LoadPastSeasonAnimeAsync(int year, Season season)
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
                    CancellationToken.None);
                AnimeList.Clear();
                foreach (var anime in list)
                {
                    AnimeList.Add(anime);
                }

                // 更新统计信息
                TotalCount = list.Count;
                HasData = list.Count > 0;
            }
            catch (Exception ex)
            {
                ErrorMessage = $"Fail to load: {ex.Message}";
                HasData = false;
                IsError = true;
            }
            finally
            {
                IsLoading = false;
            }
        }

        [RelayCommand]
        private void RetryLoad()
        {
            _ = LoadPastSeasonAnimeAsync(_lastYear, _lastSeason);
        }
    }
}
