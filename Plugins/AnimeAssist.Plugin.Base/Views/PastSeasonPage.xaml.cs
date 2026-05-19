using AniMeido.Contracts;
using AniMeido.Contracts.Models;
using AniMeido.Plugin.Base.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

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

        InitializeComboBoxes();

        ViewModel.PropertyChanged += (s, e) =>
        {
            if (e.PropertyName == nameof(PastSeasonViewModel.IsLoading))
                LoadingRing.Visibility = ViewModel.IsLoading
                    ? Microsoft.UI.Xaml.Visibility.Visible
                    : Microsoft.UI.Xaml.Visibility.Collapsed;
        };
    }

    private void InitializeComboBoxes()
    {
        int currentYear = DateTime.Now.Year;
        for (int y = 2000; y <= currentYear; y++)
            YearComboBox.Items.Add(y);
        YearComboBox.SelectedItem = currentYear;

        SeasonComboBox.Items.Add(new ComboBoxItem { Content = "冬 (1-3月)", Tag = Season.Winter });
        SeasonComboBox.Items.Add(new ComboBoxItem { Content = "春 (4-6月)", Tag = Season.Spring });
        SeasonComboBox.Items.Add(new ComboBoxItem { Content = "夏 (7-9月)", Tag = Season.Summer });
        SeasonComboBox.Items.Add(new ComboBoxItem { Content = "秋 (10-12月)", Tag = Season.Fall });

        int currentMonth = DateTime.Now.Month;
        Season currentSeason = currentMonth switch
        {
            >= 1 and <= 3 => Season.Winter,
            >= 4 and <= 6 => Season.Spring,
            >= 7 and <= 9 => Season.Summer,
            _ => Season.Fall
        };

        for (int i = 0; i < SeasonComboBox.Items.Count; i++)
        {
            if (((ComboBoxItem)SeasonComboBox.Items[i]).Tag is Season s && s == currentSeason)
            {
                SeasonComboBox.SelectedIndex = i;
                break;
            }
        }
    }

    private async void OnSearchClick(object sender, RoutedEventArgs e)
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
