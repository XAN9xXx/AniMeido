using AniMeido.Contracts;
using AniMeido.Plugin.Base.ViewModels;
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
            ViewModel = new AnimeDetailViewModel(ds);
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
                }
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
            if (e.Parameter is int animeID)
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
