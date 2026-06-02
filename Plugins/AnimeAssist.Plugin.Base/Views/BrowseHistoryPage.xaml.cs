using AniMeido.Contracts.Models;
using AniMeido.Plugin.Base.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

namespace AniMeido.Plugin.Base.Views
{
    public sealed partial class BrowseHistoryPage : Page
    {
        public BrowseHistoryViewModel ViewModel { get; }

        public BrowseHistoryPage(BrowseHistoryViewModel viewModel)
        {
            ViewModel = viewModel;
            DataContext = ViewModel;
            InitializeComponent();
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            ViewModel.LoadHistoryCommand.Execute(null);
        }

        private void OnItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is Anime anime)
                Frame.Navigate(typeof(AnimeDetailPage), anime.ID);
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
