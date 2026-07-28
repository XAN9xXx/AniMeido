using AniMeido.Contracts;
using AniMeido.Contracts.Playback;
using AniMeido.Plugin.Base.Models;
using AniMeido.Plugin.Base.Services;
using AniMeido.Plugin.Base.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AniMeido.Plugin.Base.Views;

public sealed partial class SmartListsPage : Page
{
    private readonly IPluginNavigator _navigator;
    private readonly IAnimePlaybackLauncher _playbackLauncher;
    private bool _isPlaybackAvailabilitySubscribed;

    public SmartListsPage(
        ActionCenterService actionCenter,
        TrackingService tracking,
        IAnimeDataSource dataSource,
        IPluginNavigator navigator,
        IAnimePlaybackLauncher playbackLauncher)
    {
        _navigator = navigator;
        _playbackLauncher = playbackLauncher;
        ViewModel = new SmartListsViewModel(
            actionCenter,
            tracking,
            dataSource,
            playbackLauncher.IsAvailable);
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
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    public SmartListsViewModel ViewModel { get; }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (!_isPlaybackAvailabilitySubscribed)
        {
            _playbackLauncher.AvailabilityChanged +=
                OnPlaybackAvailabilityChanged;
            _isPlaybackAvailabilitySubscribed = true;
        }

        ViewModel.SetPlaybackAvailability(
            _playbackLauncher.IsAvailable);
        await ViewModel.LoadAsync();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (!_isPlaybackAvailabilitySubscribed)
        {
            return;
        }

        _playbackLauncher.AvailabilityChanged -=
            OnPlaybackAvailabilityChanged;
        _isPlaybackAvailabilitySubscribed = false;
    }

    private void OnPlaybackAvailabilityChanged(
        object? sender,
        EventArgs e)
        => DispatcherQueue.TryEnqueue(async () =>
        {
            ViewModel.SetPlaybackAvailability(
                _playbackLauncher.IsAvailable);
            await ViewModel.LoadAsync();
        });

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
