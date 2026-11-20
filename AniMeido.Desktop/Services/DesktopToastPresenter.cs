using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace AniMeido.App.Services;

internal sealed class DesktopToastPresenter : IDisposable
{
    private readonly Grid _root;
    private readonly Grid _layer;
    private readonly StackPanel _functional;
    private readonly StackPanel _errors;
    private readonly ScrollView _functionalViewport;
    private readonly ScrollView _errorViewport;
    private readonly Dictionary<string, Border> _messages = [];
    private readonly Dictionary<Border, DispatcherQueueTimer> _timers = [];

    public DesktopToastPresenter(Grid root)
    {
        _root = root;
        _functional = new StackPanel { Spacing = 10 };
        _errors = new StackPanel { Spacing = 10 };
        _functionalViewport = CreateViewport(_functional, VerticalAlignment.Top);
        _errorViewport = CreateViewport(_errors, VerticalAlignment.Bottom);
        _layer = new Grid { Margin = new Thickness(DesktopToastLayout.OuterMargin) };
        Grid.SetRowSpan(_layer, Math.Max(1, root.RowDefinitions.Count));
        Grid.SetColumnSpan(_layer, Math.Max(1, root.ColumnDefinitions.Count));
        _layer.Children.Add(_functionalViewport);
        _layer.Children.Add(_errorViewport);
        root.Children.Add(_layer);
        root.SizeChanged += OnRootSizeChanged;
        UpdateMaximumStackHeights();
    }

    public void Show(string message, bool error, DesktopVisualStyle style, ResourceDictionary resources,
        Func<Task<bool>>? retry = null)
    {
        var key = (error ? "error:" : "functional:") + message;
        if (_messages.ContainsKey(key))
            return;
        var text = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, FontSize = 14,
            Foreground = (Brush)resources["DesktopTextBrush"], FontFamily = (FontFamily)resources["DesktopUiFontFamily"],
            VerticalAlignment = VerticalAlignment.Center };
        AutomationProperties.SetLiveSetting(text, error ? AutomationLiveSetting.Assertive : AutomationLiveSetting.Polite);
        var close = new Button { Content = "×", Style = (Style)resources["DesktopSmallActionButtonStyle"],
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0), Padding = new Thickness(error ? 6 : 2),
            MinWidth = error ? 28 : 24, MinHeight = error ? 28 : 24, VerticalAlignment = VerticalAlignment.Center,
            FontSize = error ? 18 : 14, Foreground = (Brush)resources["DesktopMutedBrush"] };
        AutomationProperties.SetName(close, "关闭提示");
        var content = new Grid { ColumnSpacing = error ? 12 : 4 };
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var closeColumn = 1;
        if (error && retry is not null)
        {
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var retryButton = new Button { Content = "重试", Padding = new Thickness(12, 6, 12, 6),
                MinWidth = 64, VerticalAlignment = VerticalAlignment.Center,
                FontFamily = (FontFamily)resources["DesktopUiFontFamily"] };
            Grid.SetColumn(retryButton, 1);
            content.Children.Add(retryButton);
            retryButton.Click += async (_, _) =>
            {
                retryButton.IsEnabled = false;
                try
                {
                    if (await retry())
                        Dismiss(message, error);
                }
                finally
                {
                    retryButton.IsEnabled = true;
                }
            };
            closeColumn = 2;
        }
        Grid.SetColumn(close, closeColumn);
        content.Children.Add(text);
        content.Children.Add(close);
        var border = new Border { Child = content,
            HorizontalAlignment = error ? HorizontalAlignment.Stretch : HorizontalAlignment.Right,
            Padding = error ? new Thickness(16, 12, 12, 12) : new Thickness(10, 5, 6, 5),
            Background = (Brush)resources["DesktopOverlayBrush"], BorderBrush = (Brush)resources["DesktopLineBrush"],
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(style switch
            {
                DesktopVisualStyle.Fluent10 => 0, DesktopVisualStyle.Paper => 12, _ => 8,
            }) };
        _messages.Add(key, border);
        var target = error ? _errors : _functional;
        if (error)
            target.Children.Add(border);
        else
            target.Children.Insert(0, border);
        (error ? _errorViewport : _functionalViewport).Visibility = Visibility.Visible;
        border.Loaded += ScrollToNewMessage;
        close.Click += (_, _) => Remove(key, border, target);
        if (!error)
        {
            var timer = _root.DispatcherQueue.CreateTimer();
            var lifetime = new DesktopToastLifetime(TimeSpan.FromSeconds(4), TimeProvider.System);
            timer.IsRepeating = false;
            timer.Tick += (_, _) => RefreshTimer();
            border.PointerEntered += (_, _) => { lifetime.SetPointerOver(true); RefreshTimer(); };
            border.PointerExited += (_, _) => { lifetime.SetPointerOver(false); RefreshTimer(); };
            border.GotFocus += (_, _) => { lifetime.SetFocusInside(true); RefreshTimer(); };
            border.LostFocus += (_, _) => { lifetime.SetFocusInside(false); RefreshTimer(); };
            _timers.Add(border, timer);
            RefreshTimer();

            void RefreshTimer()
            {
                timer.Stop();
                if (!_timers.ContainsKey(border) || lifetime.IsPaused)
                    return;
                var remaining = lifetime.Remaining;
                if (remaining == TimeSpan.Zero)
                {
                    Remove(key, border, target);
                    return;
                }
                timer.Interval = remaining;
                timer.Start();
            }
        }

        void ScrollToNewMessage(object sender, RoutedEventArgs args)
        {
            border.Loaded -= ScrollToNewMessage;
            var viewport = error ? _errorViewport : _functionalViewport;
            viewport.ScrollTo(0, error ? viewport.ScrollableHeight : 0, new ScrollingScrollOptions(ScrollingAnimationMode.Disabled));
        }
    }

    public void Dismiss(string message, bool error)
    {
        var key = (error ? "error:" : "functional:") + message;
        if (_messages.TryGetValue(key, out var border))
            Remove(key, border, error ? _errors : _functional);
    }

    public void Dispose()
    {
        _root.SizeChanged -= OnRootSizeChanged;
        foreach (var timer in _timers.Values)
            timer.Stop();
        _timers.Clear();
        _messages.Clear();
        _root.Children.Remove(_layer);
    }

    private static ScrollView CreateViewport(StackPanel content, VerticalAlignment alignment)
        => new()
        {
            Content = content, MaxWidth = 380,
            HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = alignment,
            Background = null, Visibility = Visibility.Collapsed,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            ContentOrientation = ScrollingContentOrientation.Vertical,
            HorizontalScrollMode = ScrollingScrollMode.Disabled, HorizontalScrollBarVisibility = ScrollingScrollBarVisibility.Hidden,
            VerticalScrollMode = ScrollingScrollMode.Auto, VerticalScrollBarVisibility = ScrollingScrollBarVisibility.Auto,
        };

    private void OnRootSizeChanged(object sender, SizeChangedEventArgs args) => UpdateMaximumStackHeights();

    private void UpdateMaximumStackHeights()
    {
        var maximum = DesktopToastLayout.StackMaximumHeight(_root.ActualHeight);
        _functionalViewport.MaxHeight = maximum;
        _errorViewport.MaxHeight = maximum;
    }

    private void Remove(string key, Border border, Panel target)
    {
        if (_timers.Remove(border, out var timer))
            timer.Stop();
        _messages.Remove(key);
        target.Children.Remove(border);
        if (target.Children.Count == 0)
            (ReferenceEquals(target, _errors) ? _errorViewport : _functionalViewport).Visibility = Visibility.Collapsed;
    }
}
