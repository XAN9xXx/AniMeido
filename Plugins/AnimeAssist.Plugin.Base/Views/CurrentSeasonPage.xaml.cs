using AniMeido.Contracts;
using AniMeido.Contracts.Models;
using AniMeido.Plugin.Base.ViewModels;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;

namespace AniMeido.Plugin.Base.Views
{
    public sealed partial class CurrentSeasonPage : Page
    {
        public CurrentSeasonViewModel ViewModel { get; }

        static bool _hasAutoScrolledOnce = false;

        public CurrentSeasonPage()
        {
            var ds = AppServices.Provider!.GetRequiredService<IAnimeDataSource>();
            ViewModel = new CurrentSeasonViewModel(ds);
            InitializeComponent();

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
    }
}
