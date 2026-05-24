using AniMeido.Contracts;
using AniMeido.Contracts.Models;
using AniMeido.Plugin.Base.ViewModels;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;

namespace AniMeido.Plugin.Base.Views
{
    public sealed partial class AnimeDetailPage : Page
    {
        public AnimeDetailViewModel ViewModel { get; }

        public AnimeDetailPage()
        {
            var ds = AppServices.Provider!.GetRequiredService<IAnimeDataSource>();
            var ts = AppServices.Provider!.GetRequiredService<ITrackingService>();
            ViewModel = new AnimeDetailViewModel(ds, ts);
            DataContext = ViewModel;
            InitializeComponent();

            ViewModel.PropertyChanged += (s, e) =>
            {
                switch (e.PropertyName)
                {
                    case nameof(AnimeDetailViewModel.IsLoading):
                    case nameof(AnimeDetailViewModel.IsError):
                    case nameof(AnimeDetailViewModel.HasData):
                        UpdateOverlayState();
                        break;

                    case nameof(AnimeDetailViewModel.CurrentStatus):
                        UpdateStatusHint();
                        break;

                    case nameof(AnimeDetailViewModel.IsCurrentSeason):
                    case nameof(AnimeDetailViewModel.IsOldSeason):
                        WatchingBtn.Visibility = ViewModel.IsCurrentSeason ? Visibility.Visible : Visibility.Collapsed;
                        PlanToWatchBtn.Visibility = ViewModel.IsOldSeason ? Visibility.Visible : Visibility.Collapsed;
                        break;
                }
            };

            ViewModel.LoadDetailCommand.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(AsyncRelayCommand.IsRunning))
                    UpdateOverlayState();
            };

            BangumiCard.SizeChanged += (s, e) =>
            {
                var visual = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(BangumiCard);
                visual.CenterPoint = new System.Numerics.Vector3(
                    (float)e.NewSize.Width / 2,
                    (float)e.NewSize.Height / 2, 0);
            };
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            if (e.Parameter is int animeID && animeID > 0)
                ViewModel.LoadDetailCommand.Execute(animeID);
        }

        private void UpdateOverlayState()
        {
            bool showOverlay = ViewModel.IsLoading || ViewModel.IsError;
            LoadingOverlay.Visibility = showOverlay ? Visibility.Visible : Visibility.Collapsed;
            LoadingRing.IsActive = ViewModel.IsLoading;
            ContentScrollViewer.Visibility = ViewModel.HasData ? Visibility.Visible : Visibility.Collapsed;

            if (ViewModel.IsError)
            {
                LoadingFailedImage.Visibility = Visibility.Visible;
                LoadingRing.Visibility = Visibility.Collapsed;
                LoadingHint.Text = $"{ViewModel.ErrorMessage}\n\n点击重试";
                ErrorInfoBar.Message = ViewModel.ErrorMessage;
                ErrorInfoBar.IsOpen = true;
                ErrorInfoBar.Visibility = Visibility.Visible;
            }
            else
            {
                LoadingFailedImage.Visibility = Visibility.Collapsed;
                ErrorInfoBar.IsOpen = false;
                ErrorInfoBar.Visibility = Visibility.Collapsed;
                LoadingHint.Text = ViewModel.IsLoading ? "加载中…" : "";
            }
        }

        private void OnLoadingOverlayTapped(object sender, TappedRoutedEventArgs e)
        {
            if (ViewModel.IsError)
            {
                ViewModel.RetryLoadCommand.Execute(null);
            }
        }

        private void UpdateStatusHint()
        {
            var status = ViewModel.CurrentStatus;
            ResetButtonVisuals();

            if (status == AnimeTrackingStatus.None)
            {
                StatusHint.Visibility = Visibility.Collapsed;
                return;
            }

            StatusHint.Visibility = Visibility.Visible;
            var label = status switch
            {
                AnimeTrackingStatus.Watching => "追番中",
                AnimeTrackingStatus.PlanToWatch => "补番中",
                AnimeTrackingStatus.NotInterested => "不感兴趣",
                _ => ""
            };
            StatusHint.Text = $"当前标记：{label}";

            // 高亮选中按钮
            var accent = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                (Windows.UI.Color)Application.Current.Resources["SystemAccentColor"]);
            var whiteBrush = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 255, 255, 255));

            switch (status)
            {
                case AnimeTrackingStatus.Watching:
                    SetButtonActive(WatchingBtn, WatchingIcon, WatchingText, accent, whiteBrush);
                    break;
                case AnimeTrackingStatus.PlanToWatch:
                    SetButtonActive(PlanToWatchBtn, PlanToWatchIcon, PlanToWatchText, accent, whiteBrush);
                    break;
                case AnimeTrackingStatus.NotInterested:
                    SetButtonActive(NotInterestedBtn, NotInterestedIcon, NotInterestedText, accent, whiteBrush);
                    break;
            }
        }

        private void ResetButtonVisuals()
        {
            var defaultBg = Application.Current.Resources["CardBackgroundFillColorDefault"] as Microsoft.UI.Xaml.Media.Brush;
            var secondaryBrush = Application.Current.Resources["TextFillColorSecondaryBrush"] as Microsoft.UI.Xaml.Media.Brush;

            if (defaultBg == null) defaultBg = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(25, 25, 25, 25));
            if (secondaryBrush == null) secondaryBrush = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(255, 180, 180, 180));

            SetButtonInactive(WatchingBtn, WatchingIcon, WatchingText, defaultBg, secondaryBrush);
            SetButtonInactive(PlanToWatchBtn, PlanToWatchIcon, PlanToWatchText, defaultBg, secondaryBrush);
            SetButtonInactive(NotInterestedBtn, NotInterestedIcon, NotInterestedText, defaultBg, secondaryBrush);
        }

        private void SetButtonActive(Button btn, FontIcon icon, TextBlock text,
            Microsoft.UI.Xaml.Media.Brush accentBg, Microsoft.UI.Xaml.Media.Brush whiteFg)
        {
            btn.Background = accentBg;
            btn.Foreground = whiteFg;
            btn.BorderBrush = accentBg;
            if (icon != null) icon.Foreground = whiteFg;
            if (text != null) text.Foreground = whiteFg;
        }

        private void SetButtonInactive(Button btn, FontIcon icon, TextBlock text,
            Microsoft.UI.Xaml.Media.Brush bg, Microsoft.UI.Xaml.Media.Brush fg)
        {
            btn.Background = bg;
            btn.Foreground = fg;
            btn.BorderBrush = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(60, 255, 255, 255));
            if (icon != null) icon.Foreground = fg;
            if (text != null) text.Foreground = fg;
        }

        private void OnTrackingBtnEntered(object sender, PointerRoutedEventArgs e)
        {
            if (sender is Button btn && btn.Background is Microsoft.UI.Xaml.Media.SolidColorBrush brush)
            {
                // Only apply hover if not already accent-colored
                if (brush.Color.A < 200 || brush.Color.R < 100)
                {
                    var color = (Windows.UI.Color)Application.Current.Resources["SystemAccentColor"];
                    btn.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                        Windows.UI.Color.FromArgb(25, color.R, color.G, color.B));
                }
            }
        }

        private void OnTrackingBtnExited(object sender, PointerRoutedEventArgs e)
        {
            if (sender is Button btn && btn.Background is Microsoft.UI.Xaml.Media.SolidColorBrush brush)
            {
                if (brush.Color.A < 200 || brush.Color.R < 100)
                    btn.Background = null;
            }
        }

        private async void OnBangumiCardTapped(object sender, TappedRoutedEventArgs e)
        {
            var url = ViewModel.BangumiUrl;
            if (url is not null)
            {
                await Windows.System.Launcher.LaunchUriAsync(new Uri(url));
            }
        }

        private void OnBangumiPointerEntered(object sender, PointerRoutedEventArgs e)
        {
            var visual = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(BangumiCard);
            var compositor = visual.Compositor;
            visual.Properties.InsertVector3("Translation", new System.Numerics.Vector3(0, 0, 16));
            var sx = compositor.CreateScalarKeyFrameAnimation();
            sx.InsertKeyFrame(1.0f, 1.05f); sx.Duration = TimeSpan.FromMilliseconds(200);
            var sy = compositor.CreateScalarKeyFrameAnimation();
            sy.InsertKeyFrame(1.0f, 1.05f); sy.Duration = TimeSpan.FromMilliseconds(200);
            visual.StartAnimation("Scale.X", sx);
            visual.StartAnimation("Scale.Y", sy);
        }

        private void OnBangumiPointerExited(object sender, PointerRoutedEventArgs e)
        {
            var visual = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(BangumiCard);
            var compositor = visual.Compositor;
            visual.Properties.InsertVector3("Translation", new System.Numerics.Vector3(0, 0, 0));
            var sx = compositor.CreateScalarKeyFrameAnimation();
            sx.InsertKeyFrame(1.0f, 1.0f); sx.Duration = TimeSpan.FromMilliseconds(200);
            var sy = compositor.CreateScalarKeyFrameAnimation();
            sy.InsertKeyFrame(1.0f, 1.0f); sy.Duration = TimeSpan.FromMilliseconds(200);
            visual.StartAnimation("Scale.X", sx);
            visual.StartAnimation("Scale.Y", sy);
        }

        private void OnBangumiPointerPressed(object sender, PointerRoutedEventArgs e)
        {
            var visual = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(BangumiCard);
            var compositor = visual.Compositor;
            var sx = compositor.CreateScalarKeyFrameAnimation();
            sx.InsertKeyFrame(1.0f, 0.95f); sx.Duration = TimeSpan.FromMilliseconds(100);
            var sy = compositor.CreateScalarKeyFrameAnimation();
            sy.InsertKeyFrame(1.0f, 0.95f); sy.Duration = TimeSpan.FromMilliseconds(100);
            visual.StartAnimation("Scale.X", sx);
            visual.StartAnimation("Scale.Y", sy);
        }

        private void OnBangumiPointerReleased(object sender, PointerRoutedEventArgs e)
        {
            var visual = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(BangumiCard);
            var compositor = visual.Compositor;
            var sx = compositor.CreateScalarKeyFrameAnimation();
            sx.InsertKeyFrame(1.0f, 1.05f); sx.Duration = TimeSpan.FromMilliseconds(100);
            var sy = compositor.CreateScalarKeyFrameAnimation();
            sy.InsertKeyFrame(1.0f, 1.05f); sy.Duration = TimeSpan.FromMilliseconds(100);
            visual.StartAnimation("Scale.X", sx);
            visual.StartAnimation("Scale.Y", sy);
        }
    }
}
