using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AniMeido.Contracts;
using AniMeido.Contracts.Models;
using System.Collections.ObjectModel;

namespace AniMeido.Plugin.Base.ViewModels
{
    public partial class AnimeDetailViewModel : ObservableObject
    {
        [ObservableProperty]
        private Anime? _animeDetail = null;
        [ObservableProperty]
        private bool _isLoading = false;
        [ObservableProperty]
        string? _errorMessage = null;
        private int _lastAnimeID;
        private readonly IAnimeDataSource _animeDataSource;


        public AnimeDetailViewModel(IAnimeDataSource dataSource)
        {
            _animeDataSource = dataSource;
        }

        [RelayCommand]
        private async Task LoadDetailAsync(int animeID)
        {
            IsLoading = true;
            _lastAnimeID = animeID;
            try
            {
                AnimeDetail = await _animeDataSource.GetAnimeDetailAsync(animeID, CancellationToken.None);
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
            LoadDetailCommand.Execute(_lastAnimeID);
        }
    }
}
