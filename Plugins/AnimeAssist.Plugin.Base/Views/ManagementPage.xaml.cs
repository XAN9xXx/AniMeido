using AniMeido.Contracts;
using AniMeido.Contracts.Models;
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
    public sealed partial class ManagementPage : Page
    {
        public ManagementViewModel ViewModel { get; }

        public ManagementPage()
        {
            var ts = AppServices.Provider!.GetRequiredService<TrackingService>();
            var ds = AppServices.Provider!.GetRequiredService<IAnimeDataSource>();
            ViewModel = new ManagementViewModel(ts, ds);
            DataContext = ViewModel;
            InitializeComponent();

            ViewModel.PropertyChanged += (s, e) =>
            {
                switch (e.PropertyName)
                {
                    case nameof(ManagementViewModel.IsLoading):
                        LoadingOverlay.Visibility = ViewModel.IsLoading ? Visibility.Visible : Visibility.Collapsed;
                        LoadingRing.IsActive = ViewModel.IsLoading;
                        break;

                    case nameof(ManagementViewModel.IsError):
                        if (ViewModel.IsError)
                        {
                            ErrorInfoBar.Message = ViewModel.ErrorMessage;
                            ErrorInfoBar.IsOpen = true;
                            ErrorInfoBar.Visibility = Visibility.Visible;
                        }
                        else
                        {
                            ErrorInfoBar.IsOpen = false;
                            ErrorInfoBar.Visibility = Visibility.Collapsed;
                        }
                        break;

                    case nameof(ManagementViewModel.WatchingCount):
                        WatchingCountText.Text = $"追番中 ({ViewModel.WatchingCount})";
                        WatchingEmpty.Visibility = ViewModel.WatchingCount > 0 ? Visibility.Collapsed : Visibility.Visible;
                        break;

                    case nameof(ManagementViewModel.PlanToWatchCount):
                        PlanToWatchCountText.Text = $"补番中 ({ViewModel.PlanToWatchCount})";
                        PlanToWatchEmpty.Visibility = ViewModel.PlanToWatchCount > 0 ? Visibility.Collapsed : Visibility.Visible;
                        break;

                    case nameof(ManagementViewModel.NotInterestedCount):
                        NotInterestedCountText.Text = $"不感兴趣 ({ViewModel.NotInterestedCount})";
                        NotInterestedEmpty.Visibility = ViewModel.NotInterestedCount > 0 ? Visibility.Collapsed : Visibility.Visible;
                        break;
                }
            };

            ViewModel.LoadDataCommand.Execute(null);
        }

        private void OnTabClicked(object sender, TappedRoutedEventArgs e)
        {
            WatchingPanel.Visibility = Visibility.Collapsed;
            PlanToWatchPanel.Visibility = Visibility.Collapsed;
            NotInterestedPanel.Visibility = Visibility.Collapsed;

            var transparent = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0));
            WatchingCard.Background = transparent;
            PlanToWatchCard.Background = transparent;
            NotInterestedCard.Background = transparent;

            // 重置所有指示器和文字颜色
            WatchingIndicator.Fill = (Microsoft.UI.Xaml.Media.Brush)Resources["TabIndicatorBrush"];
            WatchingIndicator.Visibility = Visibility.Collapsed;
            PlanToWatchIndicator.Fill = (Microsoft.UI.Xaml.Media.Brush)Resources["TabIndicatorBrush"];
            PlanToWatchIndicator.Visibility = Visibility.Collapsed;
            NotInterestedIndicator.Fill = (Microsoft.UI.Xaml.Media.Brush)Resources["TabIndicatorBrush"];
            NotInterestedIndicator.Visibility = Visibility.Collapsed;

            var defaultBrush = (Microsoft.UI.Xaml.Media.Brush)Resources["TabTextDefaultBrush"];
            var selectedBrush = (Microsoft.UI.Xaml.Media.Brush)Resources["TabTextSelectedBrush"];
            WatchingLabel.Foreground = defaultBrush;
            PlanToWatchLabel.Foreground = defaultBrush;
            NotInterestedLabel.Foreground = defaultBrush;

            // 设置选中项
            var accentColor = (Windows.UI.Color)Application.Current.Resources["SystemAccentColor"];
            var selectedBg = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                Windows.UI.Color.FromArgb(20, accentColor.R, accentColor.G, accentColor.B));

            if (sender == WatchingCard)
            {
                WatchingPanel.Visibility = Visibility.Visible;
                WatchingIndicator.Visibility = Visibility.Visible;
                WatchingIndicator.Fill = (Microsoft.UI.Xaml.Media.Brush)Resources["TabIndicatorBrush"];
                WatchingLabel.Foreground = selectedBrush;
                WatchingCard.Background = selectedBg;
            }
            else if (sender == PlanToWatchCard)
            {
                PlanToWatchPanel.Visibility = Visibility.Visible;
                PlanToWatchIndicator.Visibility = Visibility.Visible;
                PlanToWatchIndicator.Fill = (Microsoft.UI.Xaml.Media.Brush)Resources["TabIndicatorBrush"];
                PlanToWatchLabel.Foreground = selectedBrush;
                PlanToWatchCard.Background = selectedBg;
            }
            else if (sender == NotInterestedCard)
            {
                NotInterestedPanel.Visibility = Visibility.Visible;
                NotInterestedIndicator.Visibility = Visibility.Visible;
                NotInterestedIndicator.Fill = (Microsoft.UI.Xaml.Media.Brush)Resources["TabIndicatorBrush"];
                NotInterestedLabel.Foreground = selectedBrush;
                NotInterestedCard.Background = selectedBg;
            }
        }

        private bool IsCardSelected(Border card)
        {
            return card == WatchingCard && WatchingPanel.Visibility == Visibility.Visible
                || card == PlanToWatchCard && PlanToWatchPanel.Visibility == Visibility.Visible
                || card == NotInterestedCard && NotInterestedPanel.Visibility == Visibility.Visible;
        }

        private void OnCardPointerEntered(object sender, PointerRoutedEventArgs e)
        {
            var border = sender as Border;
            if (border != null)
            {
                var color = (Windows.UI.Color)Application.Current.Resources["SystemAccentColor"];
                byte alpha = IsCardSelected(border) ? (byte)40 : (byte)25;
                border.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                    Windows.UI.Color.FromArgb(alpha, color.R, color.G, color.B));
            }
        }

        private void OnCardPointerExited(object sender, PointerRoutedEventArgs e)
        {
            var border = sender as Border;
            if (border != null)
            {
                if (!IsCardSelected(border))
                    border.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0));
                else
                {
                    var color = (Windows.UI.Color)Application.Current.Resources["SystemAccentColor"];
                    border.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                        Windows.UI.Color.FromArgb(20, color.R, color.G, color.B));
                }
            }
        }

        private void OnCardPointerPressed(object sender, PointerRoutedEventArgs e)
        {
        }

        private void OnCardPointerReleased(object sender, PointerRoutedEventArgs e)
        {
        }

        private void OnRemoveClick(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.DataContext is Anime anime)
            {
                var tag = btn.Tag?.ToString();
                switch (tag)
                {
                    case "Watching":
                        ViewModel.RemoveFromWatchingCommand.Execute(anime.ID);
                        break;
                    case "PlanToWatch":
                        ViewModel.RemoveFromPlanCommand.Execute(anime.ID);
                        break;
                    case "NotInterested":
                        ViewModel.RemoveFromNotInterestedCommand.Execute(anime.ID);
                        break;
                }
            }
        }

        private void OnItemTapped(object sender, TappedRoutedEventArgs e)
        {
            if (sender is FrameworkElement fe && fe.DataContext is Anime anime)
            {
                Frame.Navigate(typeof(AnimeDetailPage), anime.ID);
            }
        }

        private void OnCardEntered(object sender, PointerRoutedEventArgs e)
        {
            if (sender is Border border)
            {
                var accent = (Windows.UI.Color)Application.Current.Resources["SystemAccentColor"];
                border.Background = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                    Windows.UI.Color.FromArgb(18, accent.R, accent.G, accent.B));
            }
        }

        private void OnCardExited(object sender, PointerRoutedEventArgs e)
        {
            if (sender is Border border)
                border.Background = null;
        }

        private void OnCardPressed(object sender, PointerRoutedEventArgs e)
        {
            if (sender is Border border)
            {
                var visual = ElementCompositionPreview.GetElementVisual(border);
                var compositor = visual.Compositor;

                var scaleX = compositor.CreateScalarKeyFrameAnimation();
                scaleX.InsertKeyFrame(0.0f, 1.0f);
                scaleX.InsertKeyFrame(0.6f, 0.97f);
                scaleX.InsertKeyFrame(1.0f, 1.03f);
                scaleX.Duration = TimeSpan.FromMilliseconds(300);

                var scaleY = compositor.CreateScalarKeyFrameAnimation();
                scaleY.InsertKeyFrame(0.0f, 1.0f);
                scaleY.InsertKeyFrame(0.6f, 0.97f);
                scaleY.InsertKeyFrame(1.0f, 1.03f);
                scaleY.Duration = TimeSpan.FromMilliseconds(300);

                visual.CenterPoint = new System.Numerics.Vector3(
                    (float)border.ActualWidth / 2, (float)border.ActualHeight / 2, 0);
                visual.StartAnimation("Scale.X", scaleX);
                visual.StartAnimation("Scale.Y", scaleY);
                // 弹回
                var bounceX = compositor.CreateScalarKeyFrameAnimation();
                bounceX.InsertKeyFrame(0.0f, 1.03f);
                bounceX.InsertKeyFrame(1.0f, 1.0f);
                bounceX.Duration = TimeSpan.FromMilliseconds(200);
                var bounceY = compositor.CreateScalarKeyFrameAnimation();
                bounceY.InsertKeyFrame(0.0f, 1.03f);
                bounceY.InsertKeyFrame(1.0f, 1.0f);
                bounceY.Duration = TimeSpan.FromMilliseconds(200);
                visual.StartAnimation("Scale.X", bounceX);
                visual.StartAnimation("Scale.Y", bounceY);
            }
        }

        private void OnCardReleased(object sender, PointerRoutedEventArgs e)
        {
            if (sender is Border border)
            {
                var visual = ElementCompositionPreview.GetElementVisual(border);
                var compositor = visual.Compositor;
                var reset = compositor.CreateScalarKeyFrameAnimation();
                reset.InsertKeyFrame(1.0f, 1.0f);
                reset.Duration = TimeSpan.FromMilliseconds(100);
                visual.StartAnimation("Scale.X", reset);
                visual.StartAnimation("Scale.Y", reset);
            }
        }
    }
}
