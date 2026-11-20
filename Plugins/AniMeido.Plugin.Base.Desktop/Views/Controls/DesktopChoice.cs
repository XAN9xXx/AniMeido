using AniMeido.Plugin.Base.ViewModels;
using AniMeido.Plugin.Base.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using System.Collections.ObjectModel;
using Windows.System;

namespace AniMeido.Plugin.Base.Views.Controls;

[ContentProperty(Name = nameof(Items))]
public sealed class DesktopChoice : UserControl
{
    public static readonly DependencyProperty SelectedIndexProperty = DependencyProperty.Register(
        nameof(SelectedIndex), typeof(int), typeof(DesktopChoice),
        new PropertyMetadata(-1, OnSelectedIndexChanged));

    private readonly ComboBox _native = new() { HorizontalAlignment = HorizontalAlignment.Stretch };
    private readonly ListBox _segments = new();
    private bool _synchronizing;

    public DesktopChoice()
    {
        IsTabStop = false;
        _segments.Style = (Style)Application.Current.Resources["DesktopSegmentsStyle"];
        _native.ItemsSource = Items;
        _native.SelectionChanged += OnSelectionChanged;
        _segments.SelectionChanged += OnSelectionChanged;
        _segments.AddHandler(KeyDownEvent, new KeyEventHandler(OnSegmentKeyDown), handledEventsToo: true);
        Items.CollectionChanged += (_, _) => RebuildSegments();
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

    private void RebuildSegments()
    {
        _synchronizing = true;
        try
        {
            _segments.Items.Clear();
            foreach (var text in Items)
                _segments.Items.Add(new ListBoxItem { Content = text });
        }
        finally { _synchronizing = false; }
        RefreshPresentation();
    }

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
            var index = SelectedIndex < Items.Count ? SelectedIndex : -1;
            if (_native.SelectedIndex != index) _native.SelectedIndex = index;
            if (_segments.SelectedIndex != index) _segments.SelectedIndex = index;
            // Uno 的 ListBox 自定义面板收到真实点击后可能只更新索引和 SelectedItem。
            // 用同一个索引同步保留的选项，确保绘制、读屏和设置值指向同一项。
            for (var i = 0; i < _segments.Items.Count; i++)
            {
                var item = (ListBoxItem)_segments.Items[i];
                if (item.IsSelected != (i == index))
                    item.IsSelected = i == index;
            }
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

    private void OnSegmentKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (args.Key is not (VirtualKey.Space or VirtualKey.Enter) || _segments.Visibility != Visibility.Visible
            || !_segments.IsEnabled || XamlRoot is not { } root
            || FocusManager.GetFocusedElement(root) is not ListBoxItem { IsEnabled: true } item)
            return;

        // 等宽自定义面板上的 ListBoxItem 可取得焦点，但 Uno 不一定提交空格选择。
        var index = _segments.Items.IndexOf(item);
        if (index < 0) return;
        args.Handled = true;
        _segments.SelectedIndex = index;
    }
}
