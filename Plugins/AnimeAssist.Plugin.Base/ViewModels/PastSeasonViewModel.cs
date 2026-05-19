using AniMeido.Contracts;
using AniMeido.Contracts.Models;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
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

        [RelayCommand]
        private void RetryLoad()
        {
            _ = LoadPastSeasonAnimeAsync(_lastYear, _lastSeason);
        }
    }
}
