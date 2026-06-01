using AniMeido.Contracts;
using AniMeido.Contracts.Models;
using AniMeido.Plugin.Base.Services;
using AniMeido.Plugin.Base.ViewModels;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;
using Windows.UI;

namespace AniMeido.Plugin.Base.Views
{
    public sealed partial class AnimeDetailPage : Page
    {
        public AnimeDetailViewModel ViewModel { get; }
        private SavedTagService? _savedTagService;
        private IAnimeDataSource? _dataSource;
        private int _currentAnimeId;

        public AnimeDetailPage()
        {
            var ds = AppServices.Provider!.GetRequiredService<IAnimeDataSource>();
            var ts = AppServices.Provider!.GetRequiredService<TrackingService>();
            _savedTagService = AppServices.Provider!.GetRequiredService<SavedTagService>();
            _dataSource = ds;
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
                        if (ViewModel.HasData)
                        {
                            UpdateCoverImage();
                            UpdateScore();
                        }
                        break;

                    case nameof(AnimeDetailViewModel.CurrentStatus):
                        UpdateStatusHint();
                        break;

                    case nameof(AnimeDetailViewModel.StudiosText):
                        UpdateStudios();
                        break;

                    case nameof(AnimeDetailViewModel.IsCurrentSeason):
                    case nameof(AnimeDetailViewModel.IsOldSeason):
                        WatchingBtn.Visibility = ViewModel.IsCurrentSeason ? Visibility.Visible : Visibility.Collapsed;
                        PlanToWatchBtn.Visibility = ViewModel.IsOldSeason ? Visibility.Visible : Visibility.Collapsed;
                        break;
                }
            };

            // 监听角色集合变化
            ViewModel.Characters.CollectionChanged += (s, e) => UpdateCharacters();

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
            {
                _currentAnimeId = animeID;
                ViewModel.LoadDetailCommand.Execute(animeID);
                _ = LoadBangumiTagsAsync();
            }
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
                AnimeTrackingStatus.Following => "关注中",
                AnimeTrackingStatus.Completed => "已看完",
                AnimeTrackingStatus.Dropped => "已弃番",
                AnimeTrackingStatus.Blocked => "已屏蔽",
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
                case AnimeTrackingStatus.Following:
                    SetButtonActive(FollowingBtn, FollowingIcon, FollowingText, accent, whiteBrush);
                    break;
                case AnimeTrackingStatus.Completed:
                    SetButtonActive(CompletedBtn, CompletedIcon, CompletedText, accent, whiteBrush);
                    break;
                case AnimeTrackingStatus.Dropped:
                    SetButtonActive(DroppedBtn, DroppedIcon, DroppedText, accent, whiteBrush);
                    break;
                case AnimeTrackingStatus.Blocked:
                    SetButtonActive(BlockedBtn, BlockedIcon, BlockedText, accent, whiteBrush);
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
            SetButtonInactive(FollowingBtn, FollowingIcon, FollowingText, defaultBg, secondaryBrush);
            SetButtonInactive(CompletedBtn, CompletedIcon, CompletedText, defaultBg, secondaryBrush);
            SetButtonInactive(DroppedBtn, DroppedIcon, DroppedText, defaultBg, secondaryBrush);
            SetButtonInactive(BlockedBtn, BlockedIcon, BlockedText, defaultBg, secondaryBrush);
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

        private void UpdateCoverImage()
        {
            var anime = ViewModel.AnimeDetail;
            if (anime == null) return;

            if (!string.IsNullOrEmpty(anime.CoverURL))
            {
                DetailCoverImage.Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(
                    ImageCacheHelper.GetImageUri(anime.ID, anime.CoverURL));

                if (!ImageCacheHelper.HasLocalCache(anime.ID))
                    _ = ImageCacheHelper.CacheImageAsync(anime.ID, anime.CoverURL);
            }
        }

        private void UpdateScore()
        {
            var anime = ViewModel.AnimeDetail;
            if (anime?.Score.HasValue == true && anime.Score.Value > 0)
            {
                DetailScore.Text = $"评分：{anime.Score.Value:F1}";
                DetailScore.Visibility = Visibility.Visible;
            }
            else
            {
                DetailScore.Visibility = Visibility.Collapsed;
            }
        }

        private void UpdateStudios()
        {
            if (!string.IsNullOrEmpty(ViewModel.StudiosText))
            {
                DetailStudio.Text = ViewModel.StudiosText;
                DetailStudio.Visibility = Visibility.Visible;
            }
            else
            {
                DetailStudio.Visibility = Visibility.Collapsed;
            }
        }

        private void UpdateCharacters()
        {
            CharacterPanel.Children.Clear();

            if (ViewModel.Characters.Count == 0)
            {
                CharacterSection.Visibility = Visibility.Collapsed;
                return;
            }

            CharacterSection.Visibility = Visibility.Visible;

            foreach (var character in ViewModel.Characters)
            {
                var card = CreateCharacterCard(character);
                CharacterPanel.Children.Add(card);
            }
        }

        private Border CreateCharacterCard(CharacterRole character)
        {
            // 角色头像
            var avatarBorder = new Border
            {
                Width = 64,
                Height = 64,
                CornerRadius = new CornerRadius(32),
                Background = new SolidColorBrush(Color.FromArgb(30, 128, 128, 128)),
                Child = new Image
                {
                    Width = 64,
                    Height = 64,
                    Stretch = Microsoft.UI.Xaml.Media.Stretch.UniformToFill,
                    Source = !string.IsNullOrEmpty(character.CharacterImage)
                        ? new BitmapImage(new Uri(character.CharacterImage))
                        : new BitmapImage(ImageCacheHelper.PlaceholderUri),
                }
            };
            if (avatarBorder.Child is Image img)
            {
                img.ImageFailed += (s, e) =>
                {
                    img.Source = new BitmapImage(ImageCacheHelper.PlaceholderUri);
                };
            }

            // 角色名
            var nameText = new TextBlock
            {
                Text = character.CharacterName,
                FontSize = 13,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxLines = 1,
                HorizontalAlignment = HorizontalAlignment.Center,
            };

            // 声优名
            var cvText = new TextBlock
            {
                FontSize = 11,
                Foreground = new SolidColorBrush(Color.FromArgb(160, 128, 128, 128)),
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxLines = 1,
                HorizontalAlignment = HorizontalAlignment.Center,
            };

            if (character.Actors.Count > 0)
                cvText.Text = $"CV: {character.Actors[0].Name}";
            else
                cvText.Text = "CV: —";

            var stack = new StackPanel
            {
                Width = 90,
                Spacing = 4,
                HorizontalAlignment = HorizontalAlignment.Center,
                Children = { avatarBorder, nameText, cvText }
            };

            var card = new Border
            {
                Width = 90,
                Padding = new Thickness(4),
                CornerRadius = new CornerRadius(8),
                Background = new SolidColorBrush(Color.FromArgb(10, 255, 255, 255)),
                Child = stack,
                Tag = character,
            };

            // 设置 CenterPoint 使缩放以卡片中心为基准
            card.SizeChanged += (s, e) =>
            {
                var visual = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(card);
                visual.CenterPoint = new System.Numerics.Vector3(
                    (float)e.NewSize.Width / 2,
                    (float)e.NewSize.Height / 2,
                    0);
            };

            // 点击 → 跳转声优作品页
            card.Tapped += (s, e) =>
            {
                if (s is Border b && b.Tag is CharacterRole ch && ch.Actors.Count > 0)
                {
                    Frame.Navigate(typeof(PersonSearchResultPage), (ch.Actors[0].VoiceActorId, ch.Actors[0].Name));
                }
            };

            // hover 缩放动效
            card.PointerEntered += (s, e) =>
            {
                var visual = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(card);
                var compositor = visual.Compositor;
                var sx = compositor.CreateScalarKeyFrameAnimation();
                sx.InsertKeyFrame(1.0f, 1.08f); sx.Duration = TimeSpan.FromMilliseconds(200);
                var sy = compositor.CreateScalarKeyFrameAnimation();
                sy.InsertKeyFrame(1.0f, 1.08f); sy.Duration = TimeSpan.FromMilliseconds(200);
                visual.StartAnimation("Scale.X", sx);
                visual.StartAnimation("Scale.Y", sy);
            };
            card.PointerExited += (s, e) =>
            {
                var visual = Microsoft.UI.Xaml.Hosting.ElementCompositionPreview.GetElementVisual(card);
                var compositor = visual.Compositor;
                var sx = compositor.CreateScalarKeyFrameAnimation();
                sx.InsertKeyFrame(1.0f, 1.0f); sx.Duration = TimeSpan.FromMilliseconds(200);
                var sy = compositor.CreateScalarKeyFrameAnimation();
                sy.InsertKeyFrame(1.0f, 1.0f); sy.Duration = TimeSpan.FromMilliseconds(200);
                visual.StartAnimation("Scale.X", sx);
                visual.StartAnimation("Scale.Y", sy);
            };

            return card;
        }

        private void OnDetailCoverImageFailed(object sender, ExceptionRoutedEventArgs e)
        {
            DetailCoverImage.Source = new BitmapImage(ImageCacheHelper.PlaceholderUri);
        }

        // ======== Bangumi 标签 ========

        private async Task LoadBangumiTagsAsync()
        {
            if (_dataSource == null || _currentAnimeId <= 0) return;

            List<AniMeido.Contracts.Models.Tag>? bangumiTags;
            try
            {
                bangumiTags = await _dataSource.GetTagsAsync(_currentAnimeId, CancellationToken.None);
            }
            catch
            {
                return;
            }

            if (bangumiTags == null || bangumiTags.Count == 0) return;

            var allSavedTags = await _savedTagService!.GetAllSavedTagsAsync();
            var savedSet = new HashSet<string>(allSavedTags);

            // 去重
            var distinctTags = bangumiTags
                .Select(t => t.Name)
                .Distinct()
                .ToList();

            TagContainer.Children.Clear();
            foreach (var distinctName in distinctTags)
            {
                var saved = savedSet.Contains(distinctName);
                var tagBtn = CreateTagButton(distinctName, saved);
                TagContainer.Children.Add(tagBtn);
            }
            TagContainer.Visibility = Visibility.Visible;
        }

        private Border CreateTagButton(string tagName, bool isSaved)
        {
            var textBlock = new TextBlock
            {
                Text = tagName,
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
            };

            var accentColor = (Windows.UI.Color)Application.Current.Resources["SystemAccentColor"];
            var accentBrush = new Microsoft.UI.Xaml.Media.SolidColorBrush(accentColor);
            var savedBg = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                Windows.UI.Color.FromArgb(220, accentColor.R, accentColor.G, accentColor.B));
            var unsavedBg = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                Windows.UI.Color.FromArgb(30, 128, 128, 128));
            var unsavedFg = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                Windows.UI.Color.FromArgb(200, 128, 128, 128));
            var whiteBrush = new Microsoft.UI.Xaml.Media.SolidColorBrush(
                Windows.UI.Color.FromArgb(255, 255, 255, 255));

            var border = new Border
            {
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(8, 2, 8, 2),
                Margin = new Thickness(0, 0, 4, 4),
                Tag = tagName,
                Background = isSaved ? savedBg : unsavedBg,
                Child = textBlock,
            };

            textBlock.Foreground = isSaved ? whiteBrush : unsavedFg;

            // 左键 → 跳转搜索结果页
            border.Tapped += (s, e) =>
            {
                if (s is Border b && b.Tag is string name)
                {
                    Frame.Navigate(typeof(TagSearchResultPage), name);
                }
            };

            // 右键 → 切换收藏状态
            border.RightTapped += async (s, e) =>
            {
                if (s is Border b && b.Tag is string name)
                {
                    var isCurrentlySaved = b.Background is Microsoft.UI.Xaml.Media.SolidColorBrush brush
                        && brush.Color.A > 200;

                    if (isCurrentlySaved)
                    {
                        await _savedTagService!.RemoveTagAsync(name);
                        b.Background = unsavedBg;
                        if (b.Child is TextBlock tb)
                            tb.Foreground = unsavedFg;
                    }
                    else
                    {
                        await _savedTagService!.SaveTagAsync(name);
                        b.Background = savedBg;
                        if (b.Child is TextBlock tb)
                            tb.Foreground = whiteBrush;
                    }
                }
            };

            return border;
        }
    }
}
