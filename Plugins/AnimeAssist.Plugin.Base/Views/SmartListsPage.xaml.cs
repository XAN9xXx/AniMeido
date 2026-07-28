using AniMeido.Contracts;
using AniMeido.Plugin.Base.Models;
using AniMeido.Plugin.Base.Services;
using AniMeido.Plugin.Base.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AniMeido.Plugin.Base.Views;

public sealed partial class SmartListsPage : Page
{
    private readonly IPluginNavigator _navigator;

    public SmartListsPage(
        ActionCenterService actionCenter,
        TrackingService tracking,
        IAnimeDataSource dataSource,
        IPluginNavigator navigator)
    {
        _navigator = navigator;
        ViewModel = new SmartListsViewModel(
            actionCenter,
            tracking,
            dataSource);
        InitializeComponent();
        ViewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName
                == nameof(SmartListsViewModel.ErrorMessage))
            {
                ErrorInfoBar.Message = ViewModel.ErrorMessage;
                ErrorInfoBar.IsOpen =
                    !string.IsNullOrWhiteSpace(ViewModel.ErrorMessage);
            }
        };
        Loaded += async (_, _) => await ViewModel.LoadAsync();
    }

    public SmartListsViewModel ViewModel { get; }

    private void OnNewClick(object sender, RoutedEventArgs e)
    {
        ViewModel.SelectedDefinition = null;
        ViewModel.Name = "新智能列表";
        ViewModel.Conditions =
        [
            new SmartConditionEditor(),
        ];
        ViewModel.Results.Clear();
    }

    private void OnResultClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is SmartListCandidate candidate)
        {
            _navigator.Navigate(
                typeof(AnimeDetailPage),
                candidate.AnimeId);
        }
    }
}
