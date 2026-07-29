using System.Collections.ObjectModel;
using AniMeido.Contracts;
using AniMeido.Contracts.Models;
using AniMeido.Plugin.Base.Services;
using AniMeido.Plugin.Base.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;

namespace AniMeido.Plugin.Base.Views
{
    public sealed partial class ManagementPage : Page
    {
        private readonly LocalSearchService _searchService;
        private readonly IPluginNavigator _pluginNavigator;
        private readonly ObservableCollection<SearchResult> _searchResults = [];
        private CancellationTokenSource? _searchCancellation;
        private bool _tagMode;

        public ManagementViewModel ViewModel { get; }

        public ManagementPage(
            TrackingService trackingService,
            IAnimeDataSource dataSource,
            SavedTagService savedTagService,
            LocalSearchService searchService,
            IPluginNavigator pluginNavigator)
        {
            _searchService = searchService;
            _pluginNavigator = pluginNavigator;
            ViewModel = new(
                trackingService,
                dataSource,
                savedTagService);
            DataContext = ViewModel;
            InitializeComponent();
            ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        }

        public static Microsoft.UI.Xaml.Media.ImageSource GetCoverSource(
            int animeId,
            string? coverUrl) =>
            new BitmapImage(ImageCacheHelper.GetImageUri(animeId, coverUrl))
            {
                DecodePixelWidth = 128,
            };

        public static Visibility EmptyVisibility(bool hasItems) =>
            hasItems ? Visibility.Collapsed : Visibility.Visible;

        private async void OnPageLoaded(object sender, RoutedEventArgs e)
        {
            await ViewModel.LoadDataCommand.ExecuteAsync(null);
        }

        private async void OnStatusSelectionChanged(
            object sender,
            SelectionChangedEventArgs e)
        {
            if (StatusNavigation.SelectedItem is not TrackingStatusSection section)
            {
                return;
            }

            _tagMode = false;
            SearchBox.Text = string.Empty;
            SearchResultPanel.Visibility = Visibility.Collapsed;
            TagPanel.Visibility = Visibility.Collapsed;
            StatusPanel.Visibility = Visibility.Visible;
            await ViewModel.SelectSectionAsync(section);
        }

        private void OnTagNavigationClick(object sender, RoutedEventArgs e)
        {
            _tagMode = true;
            StatusNavigation.SelectedItem = null;
            SearchBox.Text = string.Empty;
            SearchResultPanel.Visibility = Visibility.Collapsed;
            StatusPanel.Visibility = Visibility.Collapsed;
            TagPanel.Visibility = Visibility.Visible;
            ViewModel.LoadTagsCommand.Execute(null);
        }

        private void OnViewModelPropertyChanged(
            object? sender,
            System.ComponentModel.PropertyChangedEventArgs e)
        {
            switch (e.PropertyName)
            {
                case nameof(ManagementViewModel.IsLoading):
                    LoadingOverlay.Visibility = ViewModel.IsLoading
                        ? Visibility.Visible
                        : Visibility.Collapsed;
                    LoadingRing.IsActive = ViewModel.IsLoading;
                    break;
                case nameof(ManagementViewModel.IsError):
                    ErrorInfoBar.Message = ViewModel.ErrorMessage;
                    ErrorInfoBar.IsOpen = ViewModel.IsError;
                    break;
                case nameof(ManagementViewModel.HasTags):
                    TagEmptyText.Visibility = ViewModel.HasTags
                        ? Visibility.Collapsed
                        : Visibility.Visible;
                    break;
            }
        }

        private void OnAnimeTapped(object sender, TappedRoutedEventArgs e)
        {
            if (sender is FrameworkElement { DataContext: Anime anime })
            {
                _pluginNavigator.Navigate(typeof(AnimeDetailPage), anime.ID);
            }
        }

        private void OnAnimeCardEntered(
            object sender,
            PointerRoutedEventArgs e)
        {
            if (sender is Border border)
            {
                var accent =
                    (Windows.UI.Color)Application.Current.Resources[
                        "SystemAccentColor"];
                border.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                    Windows.UI.Color.FromArgb(
                        18,
                        accent.R,
                        accent.G,
                        accent.B));
            }
        }

        private void OnAnimeCardExited(
            object sender,
            PointerRoutedEventArgs e)
        {
            if (sender is Border border)
            {
                border.ClearValue(Border.BackgroundProperty);
            }
        }

        private void OnSearchKeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (e.Key != Windows.System.VirtualKey.Enter)
            {
                return;
            }

            e.Handled = true;
            _ = PerformSearchAsync();
        }

        private async Task PerformSearchAsync()
        {
            var query = SearchBox.Text;
            if (string.IsNullOrWhiteSpace(query))
            {
                HideSearchResults();
                return;
            }

            _searchCancellation?.Cancel();
            _searchCancellation = new();
            var cancellationToken = _searchCancellation.Token;
            ShowSearchResults();
            SearchResultCount.Text = "搜索中…";
            _searchResults.Clear();

            try
            {
                var results = await _searchService.SearchTrackedAsync(
                    query,
                    cancellationToken);
                SearchResultCount.Text = $"搜索结果：共 {results.Count} 条";
                foreach (var result in results)
                {
                    _searchResults.Add(result);
                }
            }
            catch (OperationCanceledException)
            {
            }
#pragma warning disable CA1031
            catch (Exception ex)
            {
                SearchResultCount.Text = $"搜索出错：{ex.Message}";
                _searchResults.Clear();
            }
#pragma warning restore CA1031
        }

        private void ShowSearchResults()
        {
            SearchResultPanel.Visibility = Visibility.Visible;
            StatusPanel.Visibility = Visibility.Collapsed;
            TagPanel.Visibility = Visibility.Collapsed;
        }

        private void HideSearchResults()
        {
            SearchResultPanel.Visibility = Visibility.Collapsed;
            _searchResults.Clear();
            TagPanel.Visibility = _tagMode
                ? Visibility.Visible
                : Visibility.Collapsed;
            StatusPanel.Visibility = _tagMode
                ? Visibility.Collapsed
                : Visibility.Visible;
        }

        private void OnSearchResultClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is SearchResult result)
            {
                _pluginNavigator.Navigate(
                    typeof(AnimeDetailPage),
                    result.Anime.ID);
            }
        }

        private void OnTagItemTapped(object sender, TappedRoutedEventArgs e)
        {
            if (sender is FrameworkElement { DataContext: TagItem tag })
            {
                ViewModel.ToggleTagCommand.Execute(tag);
            }
        }

        private void OnTagAnimeTapped(object sender, TappedRoutedEventArgs e)
        {
            if (sender is FrameworkElement { DataContext: Anime anime })
            {
                _pluginNavigator.Navigate(typeof(AnimeDetailPage), anime.ID);
            }
        }
    }
}
