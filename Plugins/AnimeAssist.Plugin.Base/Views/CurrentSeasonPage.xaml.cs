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
using Microsoft.UI.Xaml.Media;
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

        // ======== 拖放标记（动态生成） ========

        private readonly Dictionary<string, DragOverlayZone> _overlayZones = new();

        private async Task LoadDragConfigAsync()
        {
            _tracking = AppServices.Provider!.GetRequiredService<TrackingService>();
            _dragZones = await _tracking.LoadDragZoneConfigAsync();
        }

        private async void OnDragItemsStarting(object sender, DragItemsStartingEventArgs e)
        {
            if (e.Items.FirstOrDefault() is Anime anime)
            {
                // 每次拖拽重新加载确保配置最新
                if (_tracking == null)
                    _tracking = AppServices.Provider!.GetRequiredService<TrackingService>();
                _dragZones = await _tracking.LoadDragZoneConfigAsync();

                e.Data.SetData("AnimeID", anime.ID);
                e.Data.RequestedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy;
                DragOverlay.Visibility = Visibility.Visible;
                // 强制布局更新确保 ActualWidth/ActualHeight 可用
                DragOverlay.UpdateLayout();
                BuildAndShowZones();
            }
        }

        private void OnDragItemsCompleted(object sender, DragItemsCompletedEventArgs e)
        {
            DragOverlay.Visibility = Visibility.Collapsed;
        }

        private void BuildAndShowZones()
        {
            // 清除旧的动态 zone
            foreach (var kv in _overlayZones)
            {
                DragOverlay.Children.Remove(kv.Value.OuterBorder);
            }
            _overlayZones.Clear();

            var pw = DragOverlay.ActualWidth;
            var ph = DragOverlay.ActualHeight;

            foreach (var config in _dragZones)
            {
                // 追番页面不显示补番/禁用目标区
                if (config.Action == DragAction.None || config.Action == DragAction.PlanToWatch) continue;

                var label = new TextBlock
                {
                    Text = GetActionLabel(config.Action),
                    FontSize = 16,
                    FontWeight = Microsoft.UI.Text.FontWeights.Bold,
                    Foreground = new SolidColorBrush(Color.FromArgb(255, 255, 255, 255)),
                };

                var inner = new Border
                {
                    Child = label,
                    CornerRadius = new CornerRadius(8),
                    Padding = new Thickness(16, 12, 16, 12),
                    Background = new SolidColorBrush(Color.FromArgb(180, 0x44, 0x88, 0xFF)),
                    Opacity = 0.7,
                };

                var zone = new Border
                {
                    Child = inner,
                    HorizontalAlignment = HorizontalAlignment.Left,
                    VerticalAlignment = VerticalAlignment.Top,
                    Background = new SolidColorBrush(Color.FromArgb(0, 0, 0, 0)),
                    AllowDrop = true,
                    Tag = config.Id,
                };

                if (pw > 0 && ph > 0)
                {
                    zone.Width = pw * config.WidthPercent;
                    zone.Height = ph * config.HeightPercent;
                    zone.Margin = new Thickness(pw * config.XPercent, ph * config.YPercent, 0, 0);
                }

                zone.DragOver += OnZoneDragOver;
                zone.Drop += OnZoneDrop;

                DragOverlay.Children.Add(zone);
                _overlayZones[config.Id] = new DragOverlayZone(zone, inner, label);
            }
        }

        private void OnZoneDragOver(object sender, DragEventArgs e)
        {
            e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy;
            // 如果之前被 DragLeave 隐藏了，重新显示
            if (sender is Border zone && zone.Tag is string id
                && _overlayZones.TryGetValue(id, out var dz))
            {
                dz.Inner.Visibility = Visibility.Visible;
            }
        }

        private async void OnZoneDrop(object sender, DragEventArgs e)
        {
            if (_tracking == null)
                _tracking = AppServices.Provider!.GetRequiredService<TrackingService>();

            if (!e.DataView.Contains("AnimeID")) return;

            var deferral = e.GetDeferral();
            try
            {
                var animeId = Convert.ToInt32(await e.DataView.GetDataAsync("AnimeID"));
                if (sender is Border zone && zone.Tag is string id)
                {
                    var config = _dragZones.Find(z => z.Id == id);
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

        private static string GetActionLabel(DragAction action) => action switch
        {
            DragAction.Watching => "追番",
            DragAction.PlanToWatch => "补番",
            DragAction.NotInterested => "不感兴趣",
            _ => "禁用"
        };
    }

    // ======== 覆盖层 Zone 元素记录 ========

    internal record DragOverlayZone(
        Border OuterBorder,
        Border Inner,
        TextBlock Label);
}