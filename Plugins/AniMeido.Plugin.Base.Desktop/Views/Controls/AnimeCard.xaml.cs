using AniMeido.Contracts.DragDrop;
using AniMeido.Contracts.Models;
using AniMeido.Plugin.Base.Services;
using AniMeido.Plugin.Base.ViewModels;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.System;

namespace AniMeido.Plugin.Base.Views.Controls;

public enum AnimeCardDisplayMode { Poster, Compact, Detailed }

public sealed class AnimeCardClickedEventArgs(Anime anime) : EventArgs
{
    public Anime Anime { get; } = anime;
}

public sealed class AnimeCardTrackingActionEventArgs(Anime anime, AnimeTrackingStatus status) : EventArgs
{
    public Anime Anime { get; } = anime;
    public AnimeTrackingStatus Status { get; } = status;
}

public sealed partial class AnimeCard : UserControl
{
    private static readonly IReadOnlyDictionary<AnimeTrackingStatus, string> StatusLabels =
        TrackingActionDescriptor.CreateDefaults().ToDictionary(action => action.Status, action => action.ActiveLabel);
    private bool _pointerOver;
    private bool _keyboardFocus;
    private bool _pressed;
    private bool _clickCandidate;
    private bool _dragStarted;
    private Point _pressPoint;
    private Anime? _pressedAnime;

    public event EventHandler<AnimeCardClickedEventArgs>? CardClicked;
    public event EventHandler<AnimeCardTrackingActionEventArgs>? TrackingActionRequested;

    public static readonly DependencyProperty AnimeProperty = DependencyProperty.Register(
        nameof(Anime), typeof(Anime), typeof(AnimeCard), new PropertyMetadata(null, OnAnimeChanged));
    public Anime? Anime { get => (Anime?)GetValue(AnimeProperty); set => SetValue(AnimeProperty, value); }

    public static readonly DependencyProperty DisplayModeProperty = DependencyProperty.Register(
        nameof(DisplayMode), typeof(AnimeCardDisplayMode), typeof(AnimeCard),
        new PropertyMetadata(AnimeCardDisplayMode.Poster, OnAppearanceChanged));
    public AnimeCardDisplayMode DisplayMode { get => (AnimeCardDisplayMode)GetValue(DisplayModeProperty); set => SetValue(DisplayModeProperty, value); }

    public static readonly DependencyProperty CardWidthProperty = DependencyProperty.Register(
        nameof(CardWidth), typeof(double), typeof(AnimeCard), new PropertyMetadata(150d, OnCardWidthChanged));
    public double CardWidth { get => (double)GetValue(CardWidthProperty); set => SetValue(CardWidthProperty, value); }

    public static readonly DependencyProperty TrackingStatusProperty = DependencyProperty.Register(
        nameof(TrackingStatus), typeof(AnimeTrackingStatus), typeof(AnimeCard), new PropertyMetadata(AnimeTrackingStatus.None, OnPresentationChanged));
    public AnimeTrackingStatus TrackingStatus { get => (AnimeTrackingStatus)GetValue(TrackingStatusProperty); set => SetValue(TrackingStatusProperty, value); }

    public static readonly DependencyProperty ShowQuickActionsProperty = RegisterFlag(nameof(ShowQuickActions));
    public bool ShowQuickActions { get => (bool)GetValue(ShowQuickActionsProperty); set => SetValue(ShowQuickActionsProperty, value); }
    public static readonly DependencyProperty ShowWatchActionProperty = RegisterFlag(nameof(ShowWatchAction), true);
    public bool ShowWatchAction { get => (bool)GetValue(ShowWatchActionProperty); set => SetValue(ShowWatchActionProperty, value); }
    public static readonly DependencyProperty IsTrackingActionPendingProperty = RegisterFlag(nameof(IsTrackingActionPending));
    public bool IsTrackingActionPending { get => (bool)GetValue(IsTrackingActionPendingProperty); set => SetValue(IsTrackingActionPendingProperty, value); }
    public static readonly DependencyProperty ShowMediaFormatBadgeProperty = RegisterFlag(nameof(ShowMediaFormatBadge));
    public bool ShowMediaFormatBadge { get => (bool)GetValue(ShowMediaFormatBadgeProperty); set => SetValue(ShowMediaFormatBadgeProperty, value); }
    public static readonly DependencyProperty ShowSeasonDateProperty = RegisterFlag(nameof(ShowSeasonDate));
    public bool ShowSeasonDate { get => (bool)GetValue(ShowSeasonDateProperty); set => SetValue(ShowSeasonDateProperty, value); }
    public static readonly DependencyProperty ShowWeekdayBadgeProperty = RegisterFlag(nameof(ShowWeekdayBadge));
    public bool ShowWeekdayBadge { get => (bool)GetValue(ShowWeekdayBadgeProperty); set => SetValue(ShowWeekdayBadgeProperty, value); }
    public static readonly DependencyProperty ShowNotYetAiredBadgeProperty = RegisterFlag(nameof(ShowNotYetAiredBadge));
    public bool ShowNotYetAiredBadge { get => (bool)GetValue(ShowNotYetAiredBadgeProperty); set => SetValue(ShowNotYetAiredBadgeProperty, value); }
    public static readonly DependencyProperty WeekdayTextProperty = DependencyProperty.Register(
        nameof(WeekdayText), typeof(string), typeof(AnimeCard), new PropertyMetadata(null, OnPresentationChanged));
    public string? WeekdayText { get => (string?)GetValue(WeekdayTextProperty); set => SetValue(WeekdayTextProperty, value); }

    public AnimeCard()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        CoverHost.SizeChanged += (_, _) => CoverClip.Rect = new Rect(0, 0, CoverHost.ActualWidth, CoverHost.ActualHeight);
        PointerEntered += (_, _) => { _pointerOver = true; RefreshInteraction(); };
        PointerExited += (_, _) => { _pointerOver = false; ResetPress(); RefreshInteraction(); };
        PointerPressed += OnPointerPressed;
        PointerReleased += OnPointerReleased;
        PointerMoved += OnPointerMoved;
        PointerCanceled += (_, _) => { ResetPress(); RefreshInteraction(); };
        PointerCaptureLost += (_, _) => { ResetPress(); RefreshInteraction(); };
        GotFocus += OnFocusChanged;
        LostFocus += OnFocusChanged;
        KeyDown += OnKeyDown;
        DragStarting += OnDragStarting;
        AddHandler(DragOverEvent, new DragEventHandler(OnDragOver), true);
        UpdateContent();
    }

    private static DependencyProperty RegisterFlag(string name, bool value = false)
        => DependencyProperty.Register(name, typeof(bool), typeof(AnimeCard), new PropertyMetadata(value, OnPresentationChanged));

    private static void OnAnimeChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var card = (AnimeCard)sender;
        if (!ReferenceEquals(args.OldValue, args.NewValue))
        {
            card._pointerOver = card._keyboardFocus = false;
            card.ResetPress();
        }
        card.UpdateContent();
        card.ConfigureCover();
    }

    private static void OnAppearanceChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var card = (AnimeCard)sender;
        card.RefreshAppearance();
        card.ConfigureCover();
    }

    private static void OnCardWidthChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
    {
        var card = (AnimeCard)sender;
        card.RefreshAppearance();
        card.ConfigureCover();
    }

    private static void OnPresentationChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
        => ((AnimeCard)sender).UpdateContent();

    private void OnLoaded(object sender, RoutedEventArgs args)
    {
        DesktopPresentationPreferences.AppearanceChanged -= OnThemeChanged;
        DesktopPresentationPreferences.AppearanceChanged += OnThemeChanged;
        RefreshAppearance();
        ConfigureCover();
    }

    private void OnUnloaded(object sender, RoutedEventArgs args)
    {
        DesktopPresentationPreferences.AppearanceChanged -= OnThemeChanged;
        _pointerOver = _keyboardFocus = false;
        ResetPress();
        RetryRing.IsActive = false;
    }

    private void OnThemeChanged(object? sender, EventArgs args) => RefreshAppearance();

    public void RefreshAppearance()
    {
        if (!IsLoaded) return;
        var resources = Application.Current.Resources;
        var font = (FontFamily)resources["DesktopUiFontFamily"];
        var serif = font.Source.Contains("DesktopSerif", StringComparison.Ordinal);
        var compact = DisplayMode == AnimeCardDisplayMode.Compact;
        var detailed = DisplayMode == AnimeCardDisplayMode.Detailed;
        var controlSize = (double)resources["DesktopControlTextSize"];
        var dataSize = (double)resources["DesktopDataTextSize"];
        var radius = (CornerRadius)resources["DesktopCoverRadius"];
        var coverWidth = CardWidth > 0 ? CardWidth : 150;
        FontFamily = font;
        Foreground = (Brush)resources["DesktopTextBrush"];
        foreach (var text in new[] { TitleText, AirDateText, DetailedTitle, DetailedMetadata, DetailedStatus,
                     DetailedScore, ScoreText, StatusText, WeekdayTextBlock, MediaFormatText })
            text.FontFamily = font;
        CardSurface.CornerRadius = radius;
        CardSurface.BorderBrush = (Brush)resources["DesktopLineBrush"];
        CardSurface.Width = detailed ? double.NaN : coverWidth;
        CardSurface.MinHeight = detailed ? 225 : 0;
        ContentLayout.ColumnDefinitions[0].Width = new GridLength(detailed ? 150 : 0);
        ContentLayout.RowDefinitions[0].Height = detailed ? new GridLength(1, GridUnitType.Star) : GridLength.Auto;
        Grid.SetColumnSpan(CoverHost, detailed ? 1 : 2);
        CoverHost.Height = detailed ? double.NaN : DesktopAnimeCardPresentation.CompactCoverHeightFor(coverWidth);
        CoverFrame.CornerRadius = detailed ? new CornerRadius(radius.TopLeft, 0, 0, radius.BottomLeft)
            : new CornerRadius(radius.TopLeft, radius.TopRight, 0, 0);
        CaptionHost.Visibility = detailed ? Visibility.Collapsed : Visibility.Visible;
        CaptionHost.Padding = compact ? new Thickness(6, 6, 6, 8) : new Thickness(10, 8);
        DetailedHost.Visibility = detailed ? Visibility.Visible : Visibility.Collapsed;
        TitleText.FontSize = compact ? 12 : controlSize;
        TitleText.LineHeight = compact ? 17 : Math.Ceiling(controlSize * 1.4);
        TitleText.MinHeight = 2 * TitleText.LineHeight;
        TitleText.FontWeight = DetailedTitle.FontWeight = compact || serif ? FontWeights.Normal : FontWeights.SemiBold;
        DetailedTitle.FontSize = 17;
        foreach (var text in new[] { AirDateText, DetailedMetadata, DetailedStatus, ScoreText }) text.FontSize = dataSize;
        AirDateText.Visibility = compact ? Visibility.Collapsed : Visibility.Visible;
        DescriptionText.FontFamily = (FontFamily)resources["DesktopReadingFontFamily"];
        DescriptionText.FontSize = (double)resources["DesktopBodySize"];
        DescriptionText.LineHeight = (double)resources["DesktopBodyLineHeight"];
        RetryRing.Foreground = (Brush)resources["DesktopAccentBrush"];
        UpdateBadgeColors();
        RefreshInteraction();
    }

    private void UpdateContent()
    {
        if (TitleText is null) return;
        var anime = Anime;
        TitleText.Text = DetailedTitle.Text = anime?.Title ?? string.Empty;
        DescriptionText.Text = anime?.Description ?? string.Empty;
        AirDateText.Text = anime?.AirDate is not { } date ? string.Empty : ShowSeasonDate
            ? AnimeCardPresentation.FormatSeasonDate(date) : date.ToString();
        DetailedMetadata.Text = anime is null ? string.Empty
            : $"{AnimeReleaseClassifier.GetMediaFormatText(anime.MediaFormat)}  {anime.AirDate}";
        ScoreText.Text = DetailedScore.Text = DesktopAnimeCardPresentation.FormatScore(anime?.Score);
        DetailedStatus.Text = MineBrowse.StatusLabel(TrackingStatus);
        AutomationProperties.SetName(this, anime?.Title ?? string.Empty);

        var label = StatusLabels.GetValueOrDefault(TrackingStatus);
        var hasStatus = TrackingStatus is not (AnimeTrackingStatus.None or AnimeTrackingStatus.Blocked) && label is not null;
        StatusText.Text = hasStatus ? label! : string.Empty;
        StatusBadge.Visibility = Visible(hasStatus);
        CompletedDim.Visibility = Visible(TrackingStatus == AnimeTrackingStatus.Completed);
        var weekday = WeekdayText;
        if (string.IsNullOrEmpty(weekday) && ShowWeekdayBadge && anime?.AirDate is { } airDate
            && airDate.DayOfWeek == DateTime.Now.DayOfWeek)
            weekday = AnimeListPresentation.GetWeekdayName(airDate.DayOfWeek == DayOfWeek.Sunday ? 7 : (int)airDate.DayOfWeek) + "放送";
        WeekdayTextBlock.Text = weekday ?? string.Empty;
        WeekdayBadge.Visibility = Visible(!string.IsNullOrEmpty(weekday));
        var format = ShowMediaFormatBadge && anime is not null ? AnimeReleaseClassifier.GetMediaFormatBadgeText(anime.MediaFormat) : null;
        MediaFormatText.Text = format ?? string.Empty;
        MediaFormatBadge.Visibility = Visible(!string.IsNullOrEmpty(format));
        WatchActionButton.Content = TrackingStatus == AnimeTrackingStatus.Watching ? "追番中 ✓" : "追番";
        FollowActionButton.Content = TrackingStatus == AnimeTrackingStatus.Following ? "关注中 ✓" : "关注";
        WatchActionButton.Visibility = Visible(ShowWatchAction);
        Grid.SetColumn(FollowActionButton, ShowWatchAction ? 1 : 0);
        Grid.SetColumnSpan(FollowActionButton, ShowWatchAction ? 1 : 2);
        WatchActionButton.Opacity = FollowActionButton.Opacity = IsTrackingActionPending ? .55 : 1;
        if (IsLoaded) UpdateBadgeColors();
        RefreshInteraction();
    }

    private void UpdateBadgeColors()
    {
        var resources = Application.Current.Resources;
        var high = Anime?.Score is { } score && AnimeCardPresentation.IsHighScore(score);
        ScoreBadge.Background = high ? (Brush)resources["DesktopAccentBrush"] : new SolidColorBrush(Colors.Black) { Opacity = .65 };
        ScoreText.Foreground = (Brush)resources[DesktopAnimeCardPresentation.ScoreForegroundResource(high)];
        StatusBadge.Background = TrackingStatus == AnimeTrackingStatus.Watching ? (Brush)resources["DesktopAccentBrush"]
            : TrackingStatus == AnimeTrackingStatus.Following ? (Brush)resources["DesktopFollowingBrush"]
            : new SolidColorBrush(Colors.Black) { Opacity = .65 };
        StatusText.Foreground = TrackingStatus == AnimeTrackingStatus.Watching ? (Brush)resources["DesktopOnAccentBrush"]
            : TrackingStatus == AnimeTrackingStatus.Following ? new SolidColorBrush(Windows.UI.Color.FromArgb(255, 42, 29, 0))
            : new SolidColorBrush(Colors.White);
        WatchActionButton.Style = (Style)resources[TrackingStatus == AnimeTrackingStatus.Watching
            ? "DefaultButtonStyle" : "AccentButtonStyle"];
    }

    private void RefreshInteraction()
    {
        if (QuickActions is null) return;
        var show = ShowQuickActions && (_pointerOver || _keyboardFocus) && Anime is not null;
        QuickActions.Visibility = Visible(show);
        NotYetAiredBadge.Visibility = Visible(ShowNotYetAiredBadge && !show);
        ScoreBadge.Visibility = Visible(!show && DisplayMode == AnimeCardDisplayMode.Poster && Anime?.Score is > 0);
        if (!IsLoaded) return;
        var key = _pressed ? "DesktopCardPressedBrush" : _pointerOver ? "DesktopCardHoverBrush" : "DesktopCardBrush";
        // 过渡只改本卡片的刷子，避免兄弟卡片共用资源时一起变色。
        CardSurface.Background = new SolidColorBrush(((SolidColorBrush)Application.Current.Resources[key]).Color);
    }

    private static Visibility Visible(bool value) => value ? Visibility.Visible : Visibility.Collapsed;

    private void ConfigureCover()
    {
        if (Anime is { } anime)
            ManagedImageLoader.ConfigureCover(CoverImage, anime.ID, anime.CoverURL,
                DisplayMode == AnimeCardDisplayMode.Detailed ? 150 : CardWidth, OnCoverStateChanged);
        else
        {
            ManagedImageLoader.Cancel(CoverImage);
            RetryRing.IsActive = false;
            RetryOverlay.Visibility = Visibility.Collapsed;
        }
    }

    private void OnCoverStateChanged(ManagedImageLoadState state)
    {
        RetryOverlay.Visibility = Visible(state != ManagedImageLoadState.Loaded);
        RetryRing.IsActive = state == ManagedImageLoadState.Loading && IsLoaded;
        RetryRing.Visibility = Visible(state == ManagedImageLoadState.Loading);
        RetryButton.Visibility = Visible(state == ManagedImageLoadState.Failed);
    }

    private void OnRetryClick(object sender, RoutedEventArgs args)
    {
        ResetPress();
        ManagedImageLoader.Retry(CoverImage);
    }

    private void OnWatchClick(object sender, RoutedEventArgs args) => RequestTracking(AnimeTrackingStatus.Watching, args);
    private void OnFollowClick(object sender, RoutedEventArgs args) => RequestTracking(AnimeTrackingStatus.Following, args);

    private void RequestTracking(AnimeTrackingStatus status, RoutedEventArgs args)
    {
        ResetPress();
        if (!IsTrackingActionPending && Anime is { } anime)
            TrackingActionRequested?.Invoke(this, new AnimeCardTrackingActionEventArgs(anime, status));
    }

    private void OnFocusChanged(object sender, RoutedEventArgs args)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            _keyboardFocus = XamlRoot is { } root && FocusManager.GetFocusedElement(root)
                is Control { FocusState: FocusState.Keyboard } focused && BelongsToCard(focused);
            RefreshInteraction();
        });
    }

    private bool BelongsToCard(DependencyObject element)
    {
        for (DependencyObject? current = element; current is not null; current = VisualTreeHelper.GetParent(current))
            if (ReferenceEquals(current, this)) return true;
        return false;
    }

    private bool IsActionSource(object? source)
    {
        for (var current = source as DependencyObject; current is not null && !ReferenceEquals(current, this);
             current = VisualTreeHelper.GetParent(current))
            if (current is Button) return true;
        return false;
    }

    private void OnPointerPressed(object sender, PointerRoutedEventArgs args)
    {
        var point = args.GetCurrentPoint(this);
        if (!point.Properties.IsLeftButtonPressed || IsActionSource(args.OriginalSource)) return;
        _pressed = _clickCandidate = true;
        _dragStarted = false;
        _pressedAnime = Anime;
        _pressPoint = point.Position;
        RefreshInteraction();
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs args)
    {
        if (!_pressed) return;
        var position = args.GetCurrentPoint(this).Position;
        if (Math.Abs(position.X - _pressPoint.X) >= 8 || Math.Abs(position.Y - _pressPoint.Y) >= 8)
            _clickCandidate = false;
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs args)
    {
        var open = _clickCandidate && !_dragStarted && ReferenceEquals(_pressedAnime, Anime) && !IsActionSource(args.OriginalSource);
        var anime = _pressedAnime;
        ResetPress();
        RefreshInteraction();
        if (open && anime is not null) CardClicked?.Invoke(this, new AnimeCardClickedEventArgs(anime));
    }

    private void ResetPress()
    {
        _pressed = _clickCandidate = _dragStarted = false;
        _pressedAnime = null;
    }

    private void OnKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key is not (VirtualKey.Enter or VirtualKey.Space) || XamlRoot is not { } root
            || !ReferenceEquals(FocusManager.GetFocusedElement(root), this) || Anime is not { } anime) return;
        args.Handled = true;
        CardClicked?.Invoke(this, new AnimeCardClickedEventArgs(anime));
    }

    private void OnDragStarting(UIElement sender, DragStartingEventArgs args)
    {
        _dragStarted = true;
        _clickCandidate = false;
        if (Anime is not { } anime) { args.Cancel = true; return; }
        var payload = new AnimeCardDragPayload
        {
            AnimeId = anime.ID, Title = anime.Title, CoverImageUrl = anime.CoverURL,
            Summary = PersonalAnimeDataSource.IsUnavailable(anime) ? "资料不可用" : anime.Description,
            SeasonYear = anime.SeasonYear, SeasonMonth = anime.SeasonMonth, Source = "AnimeCardBody",
        };
        args.Data.SetText(AnimeCardDragPayloadSerializer.Serialize(payload));
        args.Data.RequestedOperation = args.AllowedOperations = DataPackageOperation.Copy;
        AnimeCardDragTokenVisualFactory.TryApplyDragToken(args, anime);
    }

    private void OnDragOver(object sender, DragEventArgs args)
    {
        if (!args.DataView.Contains(StandardDataFormats.Text)) return;
        args.AcceptedOperation = DataPackageOperation.Copy;
        args.Handled = true;
        args.DragUIOverride.IsCaptionVisible = args.DragUIOverride.IsGlyphVisible = false;
        args.DragUIOverride.IsContentVisible = true;
    }
}
