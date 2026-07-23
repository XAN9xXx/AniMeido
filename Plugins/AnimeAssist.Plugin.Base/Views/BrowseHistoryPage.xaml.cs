using AniMeido.Contracts;
using AniMeido.Contracts.Models;
using AniMeido.Plugin.Base.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AniMeido.Plugin.Base.Views
{
    public sealed partial class BrowseHistoryPage : Page, INavigationAware
    {
        public BrowseHistoryViewModel ViewModel { get; }
        private IPluginNavigator _pluginNavigator;

        public BrowseHistoryPage(BrowseHistoryViewModel viewModel, IPluginNavigator pluginNavigator)
        {
            ViewModel = viewModel;
            _pluginNavigator = pluginNavigator;
            DataContext = ViewModel;
            InitializeComponent();
        }

        public Task OnNavigatedToAsync(object? parameter)
        {
            ViewModel.LoadHistoryCommand.Execute(null);
            return Task.CompletedTask;
        }

        private void OnAnimeCardClicked(object? sender, Views.Controls.AnimeCardClickedEventArgs e)
        {
            _pluginNavigator.Navigate(typeof(AnimeDetailPage), e.Anime.ID);
        }

        private async void OnClearClick(object sender, RoutedEventArgs e)
        {
            if (ClearButton.XamlRoot is { } xamlRoot)
            {
                var dialog = new ContentDialog
                {
                    Title = "清空浏览记录",
                    Content = "确定要清空所有浏览记录吗？",
                    PrimaryButtonText = "确认清空",
                    CloseButtonText = "取消",
                    DefaultButton = ContentDialogButton.Close,
                    XamlRoot = xamlRoot
                };

                if (await dialog.ShowAsync() == ContentDialogResult.Primary)
                    ViewModel.ClearHistoryCommand.Execute(null);
            }
        }
    }
}
