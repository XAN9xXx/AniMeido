using AniMeido.Contracts;
using AniMeido.Contracts.Models;
using AniMeido.Contracts.Playback;
using AniMeido.Plugin.Base.Services;
using AniMeido.Plugin.Base.ViewModels;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.UI;
using System.Text.Json;

namespace AniMeido.Plugin.Base.Views
{
    public sealed partial class AnimeDetailPage : Page, INavigationAware
    {
        public AnimeDetailViewModel ViewModel { get; }
        private SavedTagService _savedTagService;
        private readonly BrowseHistoryService _browseHistory;
        private readonly IAnimeDataSource _dataSource;
        private readonly IPluginNavigator _pluginNavigator;
        private readonly IAnimePlaybackLauncher? _playbackLauncher;
        private readonly ArchiveService _archive;
        private int _currentAnimeId;
        private readonly HashSet<string> _savedTagNames = new();

        public AnimeDetailPage(
            IAnimeDataSource dataSource,
            TrackingService trackingService,
            SavedTagService savedTagService,
            BrowseHistoryService browseHistory,
            IPluginNavigator pluginNavigator,
            ArchiveService archive,
            IEnumerable<IAnimePlaybackLauncher> playbackLaunchers)
        {
            _dataSource = dataSource;
            _browseHistory = browseHistory;
            _pluginNavigator = pluginNavigator;
            _archive = archive;
            _savedTagService = savedTagService;
            _playbackLauncher = playbackLaunchers.FirstOrDefault();
            ViewModel = new AnimeDetailViewModel(dataSource, trackingService);
            DataContext = ViewModel;
            InitializeComponent();
            UpdatePlaybackAvailability();
            if (_playbackLauncher is not null)
            {
                _playbackLauncher.AvailabilityChanged += OnPlaybackAvailabilityChanged;
                Unloaded += OnPageUnloaded;
            }

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
                            RecordBrowseHistory();
                        }
                        break;

                    case nameof(AnimeDetailViewModel.StudiosText):
                        UpdateStudios();
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

        private async void OnPersonalArchiveClick(
            object sender,
            RoutedEventArgs e)
        {
            if (_currentAnimeId <= 0
                || ViewModel.AnimeDetail is not { } anime)
            {
                return;
            }

            var existing = await _archive.GetArchiveAsync(_currentAnimeId);
            var rating = new NumberBox
            {
                Header = "个人评分（0.5–10，可留空）",
                Minimum = 0.5,
                Maximum = 10,
                SmallChange = 0.5,
                Value = existing?.PersonalRating ?? double.NaN,
                SpinButtonPlacementMode =
                    NumberBoxSpinButtonPlacementMode.Compact,
            };
            var tags = new TextBox
            {
                Header = "个人标签",
                PlaceholderText = "使用逗号分隔",
                Text = string.Join(
                    ", ",
                    await _archive.GetAnimeTagsAsync(_currentAnimeId)),
            };
            var summary = new TextBox
            {
                Header = "概要笔记",
                AcceptsReturn = true,
                TextWrapping = TextWrapping.Wrap,
                MinHeight = 120,
                Text = existing?.SummaryNote ?? string.Empty,
            };
            var panel = new StackPanel { Spacing = 8 };
            panel.Children.Add(rating);
            panel.Children.Add(tags);
            panel.Children.Add(summary);
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = $"《{anime.Title}》个人档案",
                Content = panel,
                PrimaryButtonText = "保存",
                SecondaryButtonText = "打开档案馆",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Primary,
            };
            var result = await dialog.ShowAsync();
            if (result == ContentDialogResult.Secondary)
            {
                if (existing is null)
                {
                    await _archive.UpsertArchiveAsync(
                        _currentAnimeId,
                        anime.Title,
                        null,
                        string.Empty);
                }
                _pluginNavigator.Navigate(
                    typeof(ArchivePage),
                    _currentAnimeId);
                return;
            }

            if (result != ContentDialogResult.Primary)
            {
                return;
            }

            try
            {
                await _archive.UpsertArchiveAsync(
                    _currentAnimeId,
                    anime.Title,
                    double.IsNaN(rating.Value)
                        ? null
                        : rating.Value,
                    summary.Text);
                await _archive.SetAnimeTagsAsync(
                    _currentAnimeId,
                    tags.Text.Split(
                        [',', '，'],
                        StringSplitOptions.RemoveEmptyEntries
                            | StringSplitOptions.TrimEntries));
            }
            catch (ArgumentOutOfRangeException ex)
            {
                ErrorInfoBar.Message = ex.Message;
                ErrorInfoBar.IsOpen = true;
            }
        }

        private async void OnOnlinePlayClick(object sender, RoutedEventArgs e)
        {
            if (_playbackLauncher is null
                || !_playbackLauncher.IsAvailable
                || _currentAnimeId <= 0
                || ViewModel.AnimeDetail is not { } anime)
            {
                return;
            }

            try
            {
                await _playbackLauncher.LaunchAsync(
                    new AnimePlaybackContext(
                        _currentAnimeId,
                        anime.Title,
                        anime.AlternateTitles));
            }
#pragma warning disable CA1031 // Optional playback must not break the detail page.
            catch (Exception ex)
            {
                ErrorInfoBar.Message = $"无法打开在线播放器：{ex.Message}";
                ErrorInfoBar.IsOpen = true;
            }
#pragma warning restore CA1031
        }

        private void OnPlaybackAvailabilityChanged(object? sender, EventArgs e)
            => DispatcherQueue.TryEnqueue(UpdatePlaybackAvailability);

        private void OnPageUnloaded(object sender, RoutedEventArgs e)
        {
            if (_playbackLauncher is not null)
            {
                _playbackLauncher.AvailabilityChanged -= OnPlaybackAvailabilityChanged;
            }
        }

        private void UpdatePlaybackAvailability()
            => OnlinePlayButton.Visibility = _playbackLauncher?.IsAvailable == true
                ? Visibility.Visible
                : Visibility.Collapsed;

        public Task OnNavigatedToAsync(object? parameter)
        {
            if (parameter is int animeID && animeID > 0)
            {
                _currentAnimeId = animeID;
                ViewModel.LoadDetailCommand.Execute(animeID);
                _ = LoadBangumiTagsAsync();
            }
            return Task.CompletedTask;
        }

        private bool _browseRecorded;

        private void RecordBrowseHistory()
        {
            if (_browseRecorded || _currentAnimeId <= 0) return;
            _browseRecorded = true;

            var title = ViewModel.AnimeDetail?.Title ?? $"#{_currentAnimeId}";
            _ = _browseHistory.RecordAsync(_currentAnimeId, title);
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
            }
            else
            {
                LoadingFailedImage.Visibility = Visibility.Collapsed;
                ErrorInfoBar.IsOpen = false;
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

        public static Visibility BooleanToVisibility(bool value) =>
            value ? Visibility.Visible : Visibility.Collapsed;

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
            if (anime is null)
            {
                ManagedImageLoader.Cancel(DetailCoverImage);
                return;
            }

            ManagedImageLoader.ConfigureCover(
                DetailCoverImage,
                anime.ID,
                anime.CoverURL,
                300);
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
            var avatarImage = new Image
            {
                Width = 64,
                Height = 64,
                Stretch = Microsoft.UI.Xaml.Media.Stretch.UniformToFill,
            };
            ManagedImageLoader.ConfigureAvatar(
                avatarImage,
                character.CharacterImage,
                64);
            var avatarBorder = new Border
            {
                Width = 64,
                Height = 64,
                CornerRadius = new CornerRadius(32),
                Background = new SolidColorBrush(Color.FromArgb(30, 128, 128, 128)),
                Child = avatarImage,
            };

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
                    _pluginNavigator.Navigate(typeof(PersonSearchResultPage), (ch.Actors[0].VoiceActorId, ch.Actors[0].Name));
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

        // ======== Bangumi 标签 ========

        private async Task LoadBangumiTagsAsync()
        {
            if (_dataSource == null || _currentAnimeId <= 0) return;

            List<AniMeido.Contracts.Models.Tag>? bangumiTags;
            try
            {
                bangumiTags = await _dataSource.GetTagsAsync(_currentAnimeId, CancellationToken.None);
            }
            catch (HttpRequestException)
            {
                return;
            }
            catch (JsonException)
            {
                return;
            }

            if (bangumiTags == null || bangumiTags.Count == 0) return;

            var allSavedTags = await _savedTagService!.GetAllSavedTagsAsync();
            var savedSet = new HashSet<string>(allSavedTags);
            _savedTagNames.Clear();
            foreach (var t in savedSet) _savedTagNames.Add(t);

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
                    _pluginNavigator.Navigate(typeof(TagSearchResultPage), name);
                }
            };

            // 右键 → 切换收藏状态
            border.RightTapped += async (s, e) =>
            {
                if (s is Border b && b.Tag is string name)
                {
                    var isCurrentlySaved = _savedTagNames.Contains(name);

                    if (isCurrentlySaved)
                    {
                        await _savedTagService!.RemoveTagAsync(name);
                        _savedTagNames.Remove(name);
                        b.Background = unsavedBg;
                        if (b.Child is TextBlock tb)
                            tb.Foreground = unsavedFg;
                    }
                    else
                    {
                        await _savedTagService!.SaveTagAsync(name);
                        _savedTagNames.Add(name);
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
