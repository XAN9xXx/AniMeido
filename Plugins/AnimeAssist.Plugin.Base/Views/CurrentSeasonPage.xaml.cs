using AniMeido.Contracts;
using AniMeido.Contracts.Models;
using AniMeido.Plugin.Base.Models;
using AniMeido.Plugin.Base.Services;
using AniMeido.Plugin.Base.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Windows.UI;

namespace AniMeido.Plugin.Base.Views
{
    public sealed partial class CurrentSeasonPage : Page
    {
        public CurrentSeasonViewModel ViewModel { get; }

        static bool _hasAutoScrolledOnce = false;
        private List<DragZoneConfig> _dragZones = DragZoneConfig.GetDefaults();
        private TrackingService? _tracking;

        public CurrentSeasonPage()
        {
            var ds = AppServices.Provider!.GetRequiredService<IAnimeDataSource>();
            ViewModel = new CurrentSeasonViewModel(ds);
            InitializeComponent();

            // 异步加载拖放配置
            _ = LoadDragConfigAsync();

            ViewModel.PropertyChanged += (s, e) =>
            {
                switch (e.PropertyName)
                {
                    case nameof(CurrentSeasonViewModel.IsLoading):
                        UpdateOverlayState();
                        if (!ViewModel.IsLoading)
                            UpdateViewState();

                        // 首次打开时自动跳转到今天对应的星期分组
                        if (!_hasAutoScrolledOnce && !ViewModel.IsLoading && ViewModel.WeekdayGroups.Count > 0)
                        {
                            _hasAutoScrolledOnce = true;
                            int todayIndex = DateTime.Now.DayOfWeek switch
                            {
                                DayOfWeek.Sunday => 6,
                                _ => (int)DateTime.Now.DayOfWeek - 1
                            };
                            DelayedScrollToGroup(todayIndex);
                        }
                        break;

                    case nameof(CurrentSeasonViewModel.ErrorMessage):
                    case nameof(CurrentSeasonViewModel.IsError):
                        UpdateOverlayState();
                        UpdateViewState();
                        break;

                    case nameof(CurrentSeasonViewModel.HasData):
                        UpdateViewState();
                        break;
                }
            };

            ViewModel.LoadSeasonalAnimeCommand.Execute(null);
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


        private async void DelayedScrollToGroup(int index)
        {
            for (int i = 0; i < 10; i++)
            {
                if (i > 0)
                    await Task.Delay(50);

                WeekdayRepeater.UpdateLayout();

                var container = WeekdayRepeater.ContainerFromIndex(index) as UIElement;
                if (container is not null)
                {
                    container.StartBringIntoView(new BringIntoViewOptions
                    {
                        AnimationDesired = true,
                        VerticalOffset = 0
                    });
                    await Task.Delay(500);
                    PlayBringIntoViewEffect(container);
                    return;
                }
            }
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

        private void OnWeekdayItemClick(object sender, ItemClickEventArgs e)
        {
            if (e.ClickedItem is Anime anime)
                Frame.Navigate(typeof(AnimeDetailPage), anime.ID);
        }

        private static void PlayBringIntoViewEffect(UIElement element)
        {
            var visual = ElementCompositionPreview.GetElementVisual(element);
            var compositor = visual.Compositor;

            visual.CenterPoint = new System.Numerics.Vector3(
                (float)element.ActualSize.X / 2,
                (float)element.ActualSize.Y / 2,
                0);

            var scaleX = compositor.CreateScalarKeyFrameAnimation();
            scaleX.InsertKeyFrame(0.0f, 1.0f);
            scaleX.InsertKeyFrame(0.6f, 0.97f);
            scaleX.InsertKeyFrame(0.8f, 1.01f);
            scaleX.InsertKeyFrame(1.0f, 1.0f);
            scaleX.Duration = TimeSpan.FromMilliseconds(800);

            var scaleY = compositor.CreateScalarKeyFrameAnimation();
            scaleY.InsertKeyFrame(0.0f, 1.0f);
            scaleY.InsertKeyFrame(0.6f, 0.97f);
            scaleY.InsertKeyFrame(0.8f, 1.01f);
            scaleY.InsertKeyFrame(1.0f, 1.0f);
            scaleY.Duration = TimeSpan.FromMilliseconds(800);

            visual.StartAnimation("Scale.X", scaleX);
            visual.StartAnimation("Scale.Y", scaleY);
        }

        // ======== 拖放标记 ========

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
            if (config == null || config.Action == DragAction.None || config.Action == DragAction.PlanToWatch)
            {
                zone.Visibility = Visibility.Collapsed;
                return;
            }
            zone.Visibility = Visibility.Visible;
            inner.Visibility = Visibility.Visible;
            label.Text = config.Action switch
            {
                DragAction.Watching => "追番",
                DragAction.PlanToWatch => "补番",
                DragAction.NotInterested => "不感兴趣",
                _ => ""
            };
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
