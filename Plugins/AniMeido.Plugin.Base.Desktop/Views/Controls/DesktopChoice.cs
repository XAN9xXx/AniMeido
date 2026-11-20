using AniMeido.Plugin.Base.ViewModels;
using AniMeido.Plugin.Base.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using System.Collections.ObjectModel;

namespace AniMeido.Plugin.Base.Views.Controls;

[ContentProperty(Name = nameof(Items))]
public sealed class DesktopChoice : UserControl
{
    public static readonly DependencyProperty SelectedIndexProperty = DependencyProperty.Register(
        nameof(SelectedIndex), typeof(int), typeof(DesktopChoice),
        new PropertyMetadata(-1, OnSelectedIndexChanged));

    private readonly ComboBox _native = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ListBox _segments = new SegmentListBox();
    private bool _synchronizing;

    public DesktopChoice()
    {
        IsTabStop = false;
        _segments.Style = (Style)Application.Current.Resources["DesktopSegmentsStyle"];
        _native.ItemsSource = Items;
        _segments.ItemsSource = Items;
        _native.SelectionChanged += OnSelectionChanged;
        _segments.SelectionChanged += OnSelectionChanged;
        Items.CollectionChanged += (_, _) => RefreshPresentation();
        Content = new Grid { Children = { _native, _segments } };
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    public ObservableCollection<string> Items { get; } = [];

    public int SelectedIndex
    {
        get => (int)GetValue(SelectedIndexProperty);
        set => SetValue(SelectedIndexProperty, value);
    }

    public event SelectionChangedEventHandler? SelectionChanged;

    private void OnLoaded(object sender, RoutedEventArgs args)
    {
        DesktopPresentationPreferences.AppearanceChanged -= OnAppearanceChanged;
        DesktopPresentationPreferences.AppearanceChanged += OnAppearanceChanged;
        RefreshPresentation();
    }

    private void OnUnloaded(object sender, RoutedEventArgs args)
        => DesktopPresentationPreferences.AppearanceChanged -= OnAppearanceChanged;

    private void OnAppearanceChanged(object? sender, EventArgs args) => RefreshPresentation();

    public void RefreshPresentation()
    {
        var resources = Application.Current.Resources;
        var useSegments = DesktopChoicePresentation.UseSegments(
            (bool)resources["DesktopUseSegmentedChoices"], Items.Count);
        var previous = _native.Visibility == Visibility.Visible ? (Control)_native : _segments;
        var focus = previous.FocusState;
        var changed = useSegments != (_segments.Visibility == Visibility.Visible);
        _native.Visibility = useSegments ? Visibility.Collapsed : Visibility.Visible;
        _segments.Visibility = useSegments ? Visibility.Visible : Visibility.Collapsed;
        FontFamily = _native.FontFamily = _segments.FontFamily = (FontFamily)resources["DesktopUiFontFamily"];
        FontSize = _native.FontSize = _segments.FontSize = (double)resources["DesktopControlTextSize"];
        _segments.CornerRadius = (CornerRadius)resources["DesktopSegmentTrackRadius"];
        var label = AutomationProperties.GetName(this);
        AutomationProperties.SetName(_native, label);
        AutomationProperties.SetName(_segments, label);
        AutomationProperties.SetAutomationId(_native, Name + "Native");
        AutomationProperties.SetAutomationId(_segments, Name + "Segments");
        SynchronizeSelection();
        if (changed && IsLoaded && focus != FocusState.Unfocused)
            (useSegments ? (Control)_segments : _native).Focus(focus);
    }

    private static void OnSelectedIndexChanged(DependencyObject sender, DependencyPropertyChangedEventArgs args)
        => ((DesktopChoice)sender).SynchronizeSelection();

    private void SynchronizeSelection()
    {
        _synchronizing = true;
        try
        {
            _native.SelectedIndex = SelectedIndex;
            _segments.SelectedIndex = SelectedIndex;
        }
        finally
        {
            _synchronizing = false;
        }
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        var index = ((Selector)sender).SelectedIndex;
        if (_synchronizing || index == SelectedIndex)
            return;
        SelectedIndex = index;
        SelectionChanged?.Invoke(this, args);
    }

    // Uno 的普通 ListBox 为字符串创建 ContentPresenter；显式提供可选择的容器。
    private sealed class SegmentListBox : ListBox
    {
        protected override DependencyObject GetContainerForItemOverride() => new ListBoxItem();
        protected override bool IsItemItsOwnContainerOverride(object item) => item is ListBoxItem;
    }
}
