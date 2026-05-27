using AniMeido.Contracts;
using AniMeido.Contracts.Models;
using AniMeido.Plugin.Base.Models;
using AniMeido.Plugin.Base.Services;
using AniMeido.Plugin.Base.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.UI;

namespace AniMeido.Plugin.Base.Views
{
    public sealed partial class PastSeasonPage : Page
    {
        private readonly PastSeasonViewModel _viewModel;
        public PastSeasonViewModel ViewModel => _viewModel;
        private List<DragZoneConfig> _dragZones = DragZoneConfig.GetDefaults();
        private TrackingService? _tracking;

        public PastSeasonPage()
        {
            var ds = AppServices.Provider!.GetRequiredService<IAnimeDataSource>();
            _viewModel = new PastSeasonViewModel(ds);
            InitializeComponent();

            // 异步加载拖放配置
            _ = LoadDragConfigAsync();

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

        private void OnDragItemsStarting(object sender, DragItemsStartingEventArgs e)
        {
            if (e.Items.FirstOrDefault() is Anime anime)
            {
                e.Data.SetData("AnimeID", anime.ID);
                e.Data.RequestedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy;
                DragOverlay.Visibility = Visibility.Visible;
                ShowZones();
            }
        }

        private void OnDragItemsCompleted(object sender, DragItemsCompletedEventArgs e)
        {
            DragOverlay.Visibility = Visibility.Collapsed;
        }

        private void OnItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is Anime anime)
                Frame.Navigate(typeof(AnimeDetailPage), anime.ID);
        }

        private async Task LoadDragConfigAsync()
        {
            _tracking = AppServices.Provider!.GetRequiredService<TrackingService>();
            _dragZones = await _tracking.LoadDragZoneConfigAsync();
        }

        private void OnZoneDragOver(object sender, DragEventArgs e)
        {
            e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy;
            ShowZones();
        }

        private void OnZoneDragLeave(object sender, DragEventArgs e)
        {
            if (sender is Border zone)
            {
                var inner = GetZoneInner(zone.Tag?.ToString());
                if (inner != null) SetZoneHidden(inner);
            }
        }

        private async void OnZoneDrop(object sender, DragEventArgs e)
        {
            if (_tracking == null) _tracking = AppServices.Provider!.GetRequiredService<TrackingService>();

            if (e.DataView.Contains("AnimeID"))
            {
                var deferral = e.GetDeferral();
                try
                {
                    var animeId = Convert.ToInt32(await e.DataView.GetDataAsync("AnimeID"));
                    if (sender is Border zone)
                    {
                        var config = _dragZones.Find(z =>
                        {
                            var key = zone.Tag?.ToString();
                            return key == "TopLeft" && z.Position == DragPosition.TopLeft
                                || key == "TopRight" && z.Position == DragPosition.TopRight
                                || key == "BottomLeft" && z.Position == DragPosition.BottomLeft
                                || key == "BottomRight" && z.Position == DragPosition.BottomRight;
                        });
                        if (config != null && config.Action != DragAction.None)
                        {
                            var status = config.Action switch
                            {
                                DragAction.Watching => AnimeTrackingStatus.Watching,
                                DragAction.PlanToWatch => AnimeTrackingStatus.PlanToWatch,
                                DragAction.NotInterested => AnimeTrackingStatus.NotInterested,
                                _ => AnimeTrackingStatus.None
                            };
                            if (status != AnimeTrackingStatus.None)
                                await _tracking.SetStatusAsync(animeId, status);
                        }
                    }
                }
                finally
                {
                    deferral.Complete();
                }
            }
        }

        private void ShowZones()
        {
            ConfigureZone(TopLeftZone, TopLeftInner, TopLeftText, DragPosition.TopLeft);
            ConfigureZone(TopRightZone, TopRightInner, TopRightText, DragPosition.TopRight);
            ConfigureZone(BottomLeftZone, BottomLeftInner, BottomLeftText, DragPosition.BottomLeft);
            ConfigureZone(BottomRightZone, BottomRightInner, BottomRightText, DragPosition.BottomRight);
        }

        private void ConfigureZone(Border zone, Border inner, TextBlock label, DragPosition pos)
        {
            var config = _dragZones.Find(z => z.Position == pos);
            if (config == null || config.Action == DragAction.None || config.Action == DragAction.Watching)
            {
                zone.Visibility = Visibility.Collapsed;
                return;
            }
            zone.Visibility = Visibility.Visible;
            inner.Visibility = Visibility.Visible;
            var accent = (Windows.UI.Color)Application.Current.Resources["SystemAccentColor"];
            inner.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                Windows.UI.Color.FromArgb(180, accent.R, accent.G, accent.B));
            label.Text = config.Action switch
            {
                DragAction.Watching => "追番",
                DragAction.PlanToWatch => "补番",
                DragAction.NotInterested => "不感兴趣",
                _ => ""
            };

            // 根据设置同步区域大小
            var parentW = DragOverlay.ActualWidth;
            var parentH = DragOverlay.ActualHeight;
            if (parentW > 0 && parentH > 0)
            {
                zone.Width = parentW * config.SizePercent;
                zone.Height = parentH * config.SizePercent;
            }
        }

        private void HideZones()
        {
            SetZoneHidden(TopLeftInner);
            SetZoneHidden(TopRightInner);
            SetZoneHidden(BottomLeftInner);
            SetZoneHidden(BottomRightInner);
        }

        private void SetZoneHidden(Border inner)
        {
            if (inner != null)
                inner.Visibility = Visibility.Collapsed;
        }

        private Border? GetZoneInner(string? tag) => tag switch
        {
            "TopLeft" => TopLeftInner,
            "TopRight" => TopRightInner,
            "BottomLeft" => BottomLeftInner,
            "BottomRight" => BottomRightInner,
            _ => null
        };
    }
}