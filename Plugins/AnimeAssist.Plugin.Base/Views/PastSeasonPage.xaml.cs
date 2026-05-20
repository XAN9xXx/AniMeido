using AniMeido.Contracts;
using AniMeido.Contracts.Models;
using AniMeido.Plugin.Base.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;

namespace AniMeido.Plugin.Base.Views;

public sealed partial class PastSeasonPage : Page
{
    private readonly PastSeasonViewModel _viewModel;
    public PastSeasonViewModel ViewModel => _viewModel;

    public PastSeasonPage()
    {
        var ds = AppServices.Provider!.GetRequiredService<IAnimeDataSource>();
        _viewModel = new PastSeasonViewModel(ds);
        InitializeComponent();

        ViewModel.PropertyChanged += (s, e) =>
        {
            switch (e.PropertyName)
            {
                case nameof(PastSeasonViewModel.IsLoading):
                    UpdateOverlayState();
                    if (!ViewModel.IsLoading)
                        UpdateViewState();
                    break;

                case nameof(PastSeasonViewModel.ErrorMessage):
                case nameof(PastSeasonViewModel.IsError):
                    UpdateOverlayState();
                    UpdateViewState();
                    break;

                case nameof(PastSeasonViewModel.HasData):
                    UpdateViewState();
                    break;

                case nameof(PastSeasonViewModel.TotalCount):
                    if (ViewModel.TotalCount > 0)
                    {
                        StatsCard.Visibility = Visibility.Visible;
                        TotalCountText.Text = ViewModel.TotalCount.ToString();
                    }
                    else
                    {
                        StatsCard.Visibility = Visibility.Collapsed;
                    }
                    break;
            }
        };

        InitializeComboBoxes();
    }

    private void UpdateViewState()
    {
        if (ViewModel.IsError)
        {
            ErrorInfoBar.Message = ViewModel.ErrorMessage;
            ErrorInfoBar.IsOpen = true;
            ErrorInfoBar.Visibility = Visibility.Visible;
            EmptyState.Visibility = Visibility.Collapsed;
        }
        else
        {
            ErrorInfoBar.IsOpen = false;
            ErrorInfoBar.Visibility = Visibility.Collapsed;
            EmptyState.Visibility = !ViewModel.IsLoading && !ViewModel.HasData
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
    }

    private void UpdateOverlayState()
    {
        bool showOverlay = ViewModel.IsLoading || ViewModel.IsError;
        LoadingOverlay.Visibility = showOverlay ? Visibility.Visible : Visibility.Collapsed;
        LoadingRing.IsActive = ViewModel.IsLoading;

        if (ViewModel.IsError)
        {
            LoadingFailedImage.Visibility = Visibility.Visible;
            LoadingRing.Visibility = Visibility.Collapsed;
            LoadingHint.Text = $"{ViewModel.ErrorMessage}\n\n点击重试";
        }
        else if (ViewModel.IsLoading)
        {
            LoadingFailedImage.Visibility = Visibility.Collapsed;
            LoadingRing.Visibility = Visibility.Visible;
            LoadingHint.Text = "加载中…";
        }
        else
        {
            LoadingFailedImage.Visibility = Visibility.Collapsed;
            LoadingHint.Text = "";
        }
    }

    private void OnLoadingOverlayTapped(object sender, TappedRoutedEventArgs e)
    {
        if (ViewModel.IsError)
        {
            ViewModel.RetryLoadCommand.Execute(null);
        }
    }

    private void InitializeComboBoxes()
    {
        int currentYear = DateTime.Now.Year;
        for (int y = 2000; y <= currentYear; y++)
            YearComboBox.Items.Add(y);
        YearComboBox.SelectedItem = currentYear;

        RebuildSeasonItems(currentYear);

        YearComboBox.SelectionChanged += OnYearSelectionChanged;
        SeasonComboBox.SelectionChanged += OnSeasonSelectionChanged;

        // 触发初始加载
        if (YearComboBox.SelectedItem is int year &&
            SeasonComboBox.SelectedItem is ComboBoxItem item && item.Tag is Season season)
        {
            _ = ViewModel.LoadPastSeasonAnimeAsync(year, season);
        }
    }

    private void RebuildSeasonItems(int year)
    {
        SeasonComboBox.SelectionChanged -= OnSeasonSelectionChanged;
        SeasonComboBox.Items.Clear();

        var allSeasons = new[] { Season.Winter, Season.Spring, Season.Summer, Season.Fall };
        var validSeasons = year < DateTime.Now.Year
            ? allSeasons
            : allSeasons.TakeWhile(s => s <= GetCurrentSeason()).ToArray();

        foreach (var season in validSeasons)
        {
            SeasonComboBox.Items.Add(new ComboBoxItem
            {
                Content = season switch
                {
                    Season.Winter => "冬 (1-3月)",
                    Season.Spring => "春 (4-6月)",
                    Season.Summer => "夏 (7-9月)",
                    Season.Fall => "秋 (10-12月)",
                    _ => season.ToString()
                },
                Tag = season
            });
        }

        // 选中当前季度（如果可用），否则选中最后一个
        var currentSeason = GetCurrentSeason();
        for (int i = 0; i < SeasonComboBox.Items.Count; i++)
        {
            if (((ComboBoxItem)SeasonComboBox.Items[i]).Tag is Season s && s == currentSeason)
            {
                SeasonComboBox.SelectedIndex = i;
                SeasonComboBox.SelectionChanged += OnSeasonSelectionChanged;
                return;
            }
        }
        SeasonComboBox.SelectedIndex = SeasonComboBox.Items.Count - 1;
        SeasonComboBox.SelectionChanged += OnSeasonSelectionChanged;
    }

    private static Season GetCurrentSeason()
    {
        return DateTime.Now.Month switch
        {
            >= 1 and <= 3 => Season.Winter,
            >= 4 and <= 6 => Season.Spring,
            >= 7 and <= 9 => Season.Summer,
            _ => Season.Fall
        };
    }

    private void OnYearSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (YearComboBox.SelectedItem is not int year) return;
        RebuildSeasonItems(year);

        // 年份变更后立即加载新季度数据
        if (SeasonComboBox.SelectedItem is ComboBoxItem item && item.Tag is Season season)
        {
            _ = ViewModel.LoadPastSeasonAnimeAsync(year, season);
        }
    }

    private async void OnSeasonSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (YearComboBox.SelectedItem is not int year) return;
        if (SeasonComboBox.SelectedItem is not ComboBoxItem item || item.Tag is not Season season) return;
        await ViewModel.LoadPastSeasonAnimeAsync(year, season);
    }

    private void OnItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is Anime anime)
            Frame.Navigate(typeof(AnimeDetailPage), anime.ID);
    }
}
