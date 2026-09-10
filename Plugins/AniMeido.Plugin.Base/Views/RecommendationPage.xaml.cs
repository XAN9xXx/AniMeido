using AniMeido.Contracts;
using AniMeido.Plugin.Base.Models;
using AniMeido.Plugin.Base.Services;
using AniMeido.Plugin.Base.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace AniMeido.Plugin.Base.Views;

public sealed partial class RecommendationPage : Page, INavigationAware
{
    private readonly IPluginNavigator _navigator;
    private CancellationTokenSource? _navigationCancellation;
    private bool _isActionRunning;

    public RecommendationPage(
        RecommendationService recommendations,
        IPluginNavigator navigator)
    {
        _navigator = navigator;
        ViewModel = new RecommendationViewModel(recommendations);
        InitializeComponent();
        ViewModel.PropertyChanged += OnViewModelPropertyChanged;
        Unloaded += OnUnloaded;
        ShowSection("recommendations");
    }

    public RecommendationViewModel ViewModel { get; }

    public async Task OnNavigatedToAsync(object? parameter)
    {
        _navigationCancellation?.Cancel();
        _navigationCancellation?.Dispose();
        _navigationCancellation = new CancellationTokenSource();
        await ViewModel.LoadAsync(_navigationCancellation.Token);
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _navigationCancellation?.Cancel();
        _navigationCancellation?.Dispose();
        _navigationCancellation = null;
    }

    private async void OnRefreshClick(object sender, RoutedEventArgs e)
        => await RunActionAsync(() => ViewModel.RefreshAsync(
            CurrentToken,
            preferNewBatch: true));

    private void OnSectionButtonClick(object sender, RoutedEventArgs e)
    {
        if (sender is ToggleButton { Tag: string section })
        {
            ShowSection(section);
        }
    }

    private void ShowSection(string section)
    {
        RecommendationsPanel.Visibility = section == "recommendations"
            ? Visibility.Visible
            : Visibility.Collapsed;
        ProfilePanel.Visibility = section == "profile"
            ? Visibility.Visible
            : Visibility.Collapsed;
        HiddenPanel.Visibility = section == "hidden"
            ? Visibility.Visible
            : Visibility.Collapsed;
        RecommendationsSectionButton.IsChecked = section == "recommendations";
        ProfileSectionButton.IsChecked = section == "profile";
        HiddenSectionButton.IsChecked = section == "hidden";
    }

    private void OnAnimeCardClicked(
        object? sender,
        Controls.AnimeCardClickedEventArgs e)
        => _navigator.Navigate(typeof(AnimeDetailPage), e.Anime.ID);

    private void OnViewDetailsClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: RecommendationItem item })
        {
            _navigator.Navigate(typeof(AnimeDetailPage), item.Anime.ID);
        }
    }

    private async void OnHideClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: RecommendationItem item })
        {
            await RunActionAsync(() => ViewModel.HideAsync(item, CurrentToken));
        }
    }

    private async void OnNotInterestedClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: RecommendationItem item })
        {
            return;
        }

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "标记为不感兴趣",
            Content = $"“{item.Anime.Title}”会写入追番状态，并从推荐中排除。",
            PrimaryButtonText = "确认",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            await RunActionAsync(() =>
                ViewModel.MarkNotInterestedAsync(item, CurrentToken));
        }
    }

    private async void OnReasonClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: RecommendationFeature feature })
        {
            await RunActionAsync(() => ShowPreferenceDialogAsync(feature));
        }
    }

    private async void OnPreferenceClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: RecommendationFeature feature })
        {
            await RunActionAsync(() => ShowPreferenceDialogAsync(feature));
        }
    }

    private async Task ShowPreferenceDialogAsync(
        RecommendationFeature feature)
    {
        var choices = new ComboBox
        {
            Header = $"此{feature.KindText}的推荐倾向",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            SelectedIndex = 1,
        };
        choices.Items.Add("喜欢");
        choices.Items.Add("中立");
        choices.Items.Add("减少");
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = feature.DisplayName,
            Content = choices,
            PrimaryButtonText = "应用",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
        };
        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary)
        {
            return;
        }

        var adjustment = choices.SelectedIndex switch
        {
            0 => RecommendationAdjustment.Like,
            2 => RecommendationAdjustment.Reduce,
            _ => (RecommendationAdjustment?)null,
        };
        await ViewModel.SetPreferenceAsync(
            feature,
            adjustment,
            CurrentToken);
    }

    private void OnOnboardingTagToggle(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleButton toggle)
        {
            return;
        }

        if (toggle.DataContext is not RecommendationTagOption option)
        {
            return;
        }

        option.IsSelected = toggle.IsChecked == true;
        UpdateOnboardingControls();
    }

    private void OnRefreshSuggestedTagsClick(object sender, RoutedEventArgs e)
    {
        ViewModel.RefreshSuggestedTags();
        UpdateOnboardingControls();
    }

    private void UpdateOnboardingControls()
    {
        var selectedCount = ViewModel.SelectedOnboardingTags.Count;
        GenerateFromTagsButton.IsEnabled = selectedCount > 0;
        GenerateFromTagsButton.Content = selectedCount > 0
            ? $"使用所选 {selectedCount} 个标签生成推荐"
            : "使用所选标签生成推荐";
    }

    private async void OnGenerateFromTagsClick(
        object sender,
        RoutedEventArgs e)
    {
        var selected = ViewModel.SelectedOnboardingTags;
        if (selected.Count == 0)
        {
            return;
        }

        GenerateFromTagsButton.IsEnabled = false;
        await RunActionAsync(() =>
            ViewModel.ApplyOnboardingTagsAsync(selected, CurrentToken));
        GenerateFromTagsButton.IsEnabled = ViewModel.IsColdStart
            && ViewModel.SelectedOnboardingTags.Count > 0;
    }

    private async void OnRestoreClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: RecommendationHiddenAnime item })
        {
            await RunActionAsync(() => ViewModel.RestoreAsync(item, CurrentToken));
        }
    }

    private async void OnClearHiddenClick(object sender, RoutedEventArgs e)
    {
        if (ViewModel.HiddenAnime.Count == 0)
        {
            return;
        }

        var dialog = CreateConfirmationDialog(
            "恢复全部隐藏作品",
            "这些作品会在下次刷新时重新参与推荐。",
            "全部恢复");
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            await RunActionAsync(() => ViewModel.ClearHiddenAsync(CurrentToken));
        }
    }

    private async void OnClearPreferencesClick(object sender, RoutedEventArgs e)
    {
        var dialog = CreateConfirmationDialog(
            "清除全部手工偏好",
            "系统推断仍会保留，推荐将按本地记录重新计算。",
            "清除并刷新");
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            await RunActionAsync(() =>
                ViewModel.ClearPreferencesAsync(CurrentToken));
        }
    }

    private ContentDialog CreateConfirmationDialog(
        string title,
        string content,
        string primaryText)
        => new()
        {
            XamlRoot = XamlRoot,
            Title = title,
            Content = content,
            PrimaryButtonText = primaryText,
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
        };

    private void OnViewModelPropertyChanged(
        object? sender,
        System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(RecommendationViewModel.Message)
            or nameof(RecommendationViewModel.HasError))
        {
            StatusInfoBar.Message = ViewModel.Message ?? string.Empty;
            StatusInfoBar.Severity = ViewModel.HasError
                ? InfoBarSeverity.Error
                : InfoBarSeverity.Informational;
            StatusInfoBar.IsOpen = !string.IsNullOrWhiteSpace(
                ViewModel.Message);
        }
        else if (e.PropertyName
            == nameof(RecommendationViewModel.IsRefreshing))
        {
            RefreshButton.IsEnabled = !ViewModel.IsRefreshing;
        }
    }

    private CancellationToken CurrentToken
        => _navigationCancellation?.Token ?? CancellationToken.None;

    private async Task RunActionAsync(Func<Task> action)
    {
        if (_isActionRunning)
        {
            return;
        }

        _isActionRunning = true;
        var cancellationToken = CurrentToken;
        try
        {
            await action();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
#pragma warning disable CA1031 // UI 边界将可恢复错误转为页面提示，避免 async void 终止进程。
        catch (Exception ex)
        {
            ViewModel.ReportError(ex.Message);
        }
#pragma warning restore CA1031
        finally
        {
            _isActionRunning = false;
        }
    }
}
