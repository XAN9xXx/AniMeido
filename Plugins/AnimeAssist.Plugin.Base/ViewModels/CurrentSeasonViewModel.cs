using AnimeAssist.Contracts;
using AnimeAssist.Contracts.Models;
using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace AnimeAssist.Plugin.Base.ViewModels
{
    public partial class CurrentSeasonViewModel : ObservableObject
    {
        [ObservableProperty]
        private ObservableCollection<Anime> _animeList = [];
        [ObservableProperty]
        private bool _isLoading = false;
        [ObservableProperty]
        string? _errorMessage = null;



        IAnimeDataSource _animeDataSource;
        public CurrentSeasonViewModel(IAnimeDataSource dataSource)
        {
            _animeDataSource = dataSource;
        }

        [RelayCommand]
        private async Task LoadSeasonalAnimeAsync()
        {
            IsLoading = true;
            try
            {
                var list = await _animeDataSource.GetSeasonalAnimeAsync(
                    2026,
                    Season.Spring,
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
    }
}
