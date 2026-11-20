using AniMeido.Plugin.Base.Models;
using AniMeido.Plugin.Base.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.System;

namespace AniMeido.Plugin.Base.Views;

public sealed partial class DragZoneSettingsPage : Page
{
    private readonly PersonalFeatureStateStore _store;
    private readonly ILogger<DragZoneSettingsPage> _logger;
    private List<DragZoneConfig> _dragZones = DragZoneConfig.GetDefaults();
    private readonly Dictionary<string, Border> _visuals = [];
    private bool _applying;
    private bool _hasUnsavedChanges;
    private int _visit;
    private int _revision;
    private Gesture? _gesture;
    private bool _suppressTap;
    private Task _save = Task.CompletedTask;
    internal Task PendingActivation { get; private set; } = Task.CompletedTask;
    internal Task PendingOperation => _save;

    public DragZoneSettingsPage(PersonalFeatureStateStore featureState, ILogger<DragZoneSettingsPage> logger)
    {
        _store = featureState; _logger = logger;
        InitializeComponent();
        PreviewFrame.Content = new DragZonePreviewPage();
        Loaded += (_, _) => { DesktopPresentationPreferences.AppearanceChanged += OnAppearanceChanged; PendingActivation = LoadAsync(); };
        Unloaded += (_, _) => { ++_visit; DesktopPresentationPreferences.AppearanceChanged -= OnAppearanceChanged; CancelGesture(); };
    }
    private async Task LoadAsync()
    {
        var visit = ++_visit;
        var revision = _revision;
        await _save;
        if (_hasUnsavedChanges)
        {
            if (IsLoaded && visit == _visit) { Rebuild(); Feedback("设置尚未保存，请重试。", true); RetrySave.Visibility = Visibility.Visible; }
            return;
        }
        try
        {
            var zones = await _store.LoadDragZoneConfigAsync();
            if (!IsLoaded || visit != _visit || revision != _revision) return;
            _dragZones = zones; Rebuild();
        }
        catch (Exception error) when (error is Microsoft.Data.Sqlite.SqliteException or IOException
            or InvalidOperationException or System.Text.Json.JsonException)
        { _logger.LogWarning(error, "Drag zones failed to load"); if (visit == _visit) Feedback("读取设置失败，请重新打开本页。", true); }
    }
    private void Feedback(string message, bool error = false)
    { ZoneStatus.Message = message; ZoneStatus.Severity = error ? InfoBarSeverity.Error : InfoBarSeverity.Success; ZoneStatus.IsOpen = true; }

    private void Rebuild()
    {
        _applying = true;
        try
        {
            foreach (var visual in _visuals.Values) PreviewCanvas.Children.Remove(visual);
            _visuals.Clear();
            foreach (var zone in _dragZones)
            {
                var label = new TextBlock { Text = Label(zone.Action), TextWrapping = TextWrapping.Wrap,
                    Foreground = (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"], Margin = new(8), IsHitTestVisible = false };
                var visual = new Border { Child = label, Tag = zone.Id, CornerRadius = new(8), BorderThickness = new(2),
                    Background = (Brush)Application.Current.Resources["DesktopSelectedBrush"], BorderBrush = (Brush)Application.Current.Resources["DesktopAccentBrush"],
                    IsTabStop = true };
                Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(visual, $"{zone.Label}，用方向键移动，Shift 加方向键调整大小，回车切换动作");
                visual.PointerPressed += OnZonePressed; visual.PointerMoved += OnZoneMoved;
                visual.PointerReleased += OnZoneReleased; visual.PointerCanceled += (_, _) => CancelGesture();
                visual.PointerCaptureLost += (_, _) => CancelGesture();
                visual.Tapped += (_, _) => { if (_suppressTap) { _suppressTap = false; return; } Cycle(zone.Id); };
                visual.KeyDown += OnZoneKeyDown;
                _visuals.Add(zone.Id, visual); PreviewCanvas.Children.Add(visual);
            }
            ZoneConfigList.ItemsSource = _dragZones.Select(zone => new ZoneRow(zone.Id, zone.Label, (int)zone.Action)).ToArray();
            PositionZones();
        }
        finally { _applying = false; }
    }
    private void OnPreviewSizeChanged(object sender, SizeChangedEventArgs args)
    {
        PreviewBorder.Height = Math.Clamp(args.NewSize.Width * 9 / 16, 200, 400);
        PositionZones();
    }
    private void PositionZones()
    {
        foreach (var zone in _dragZones)
        {
            if (!_visuals.TryGetValue(zone.Id, out var visual)) continue;
            visual.Width = PreviewCanvas.ActualWidth * zone.WidthPercent;
            visual.Height = PreviewCanvas.ActualHeight * zone.HeightPercent;
            Canvas.SetLeft(visual, PreviewCanvas.ActualWidth * zone.XPercent);
            Canvas.SetTop(visual, PreviewCanvas.ActualHeight * zone.YPercent);
            ((TextBlock)visual.Child).Text = Label(zone.Action);
        }
    }
    private void OnAppearanceChanged(object? sender, EventArgs args)
    {
        foreach (var visual in _visuals.Values)
        {
            visual.Background = (Brush)Application.Current.Resources["DesktopSelectedBrush"];
            visual.BorderBrush = (Brush)Application.Current.Resources["DesktopAccentBrush"];
            ((TextBlock)visual.Child).Foreground = (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"];
        }
    }
    private static string Edge(Border visual, Point point)
        => (point.Y <= 10 ? "top" : point.Y >= visual.ActualHeight - 10 ? "bottom" : "")
            + (point.X <= 10 ? "left" : point.X >= visual.ActualWidth - 10 ? "right" : "");
    private void OnZonePressed(object sender, PointerRoutedEventArgs args)
    {
        if (sender is not Border { Tag: string id } visual || !args.GetCurrentPoint(visual).Properties.IsLeftButtonPressed) return;
        if (_dragZones.FirstOrDefault(z => z.Id == id) is not { } zone) return;
        visual.Focus(FocusState.Pointer);
        _suppressTap = false;
        _gesture = new(id, Clone(zone), args.GetCurrentPoint(PreviewCanvas).Position, Edge(visual, args.GetCurrentPoint(visual).Position));
        visual.CapturePointer(args.Pointer); args.Handled = true;
    }
    private void OnZoneMoved(object sender, PointerRoutedEventArgs args)
    {
        if (_gesture is not { } gesture || PreviewCanvas.ActualWidth <= 0 || PreviewCanvas.ActualHeight <= 0) return;
        var point = args.GetCurrentPoint(PreviewCanvas).Position;
        var dx = (point.X - gesture.Start.X) / PreviewCanvas.ActualWidth;
        var dy = (point.Y - gesture.Start.Y) / PreviewCanvas.ActualHeight;
        if (Math.Abs(point.X - gesture.Start.X) + Math.Abs(point.Y - gesture.Start.Y) < 3) return;
        _suppressTap = true;
        var old = gesture.Original;
        var x = old.XPercent; var y = old.YPercent; var w = old.WidthPercent; var h = old.HeightPercent;
        if (gesture.Edge.Length == 0) { x += dx; y += dy; }
        else
        {
            if (gesture.Edge.Contains("left", StringComparison.Ordinal)) { w -= dx; x += dx; }
            if (gesture.Edge.Contains("right", StringComparison.Ordinal)) w += dx;
            if (gesture.Edge.Contains("top", StringComparison.Ordinal)) { h -= dy; y += dy; }
            if (gesture.Edge.Contains("bottom", StringComparison.Ordinal)) h += dy;
        }
        SetBounds(gesture.Id, x, y, w, h, save: false); args.Handled = true;
    }
    private void OnZoneReleased(object sender, PointerRoutedEventArgs args)
    {
        if (_gesture is null) return;
        _gesture = null;
        if (sender is Border visual) visual.ReleasePointerCapture(args.Pointer);
        if (_suppressTap) _ = QueueSaveAsync();
        args.Handled = true;
    }
    private void CancelGesture()
    {
        if (_gesture is not { } gesture) return;
        _gesture = null;
        var index = _dragZones.FindIndex(zone => zone.Id == gesture.Id);
        if (index >= 0) _dragZones[index] = gesture.Original;
        PositionZones();
    }
    private void OnZoneKeyDown(object sender, KeyRoutedEventArgs args)
    {
        if (sender is not Border { Tag: string id } || _dragZones.FirstOrDefault(z => z.Id == id) is not { } zone) return;
        if (args.Key is VirtualKey.Enter or VirtualKey.Space) { Cycle(id); args.Handled = true; return; }
        if (args.Key == VirtualKey.Delete) { DeleteZone(id); args.Handled = true; return; }
        var dx = args.Key == VirtualKey.Left ? -0.01 : args.Key == VirtualKey.Right ? 0.01 : 0;
        var dy = args.Key == VirtualKey.Up ? -0.01 : args.Key == VirtualKey.Down ? 0.01 : 0;
        if (dx == 0 && dy == 0) return;
        var resize = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Shift).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        SetBounds(id, zone.XPercent + (resize ? 0 : dx), zone.YPercent + (resize ? 0 : dy),
            zone.WidthPercent + (resize ? dx : 0), zone.HeightPercent + (resize ? dy : 0)); args.Handled = true;
    }
    internal void SetBounds(string id, double x, double y, double width, double height, bool save = true)
    {
        if (_dragZones.FirstOrDefault(z => z.Id == id) is not { } zone) return;
        zone.WidthPercent = Math.Clamp(width, 0.08, 0.6); zone.HeightPercent = Math.Clamp(height, 0.08, 0.6);
        zone.XPercent = Math.Clamp(x, 0, 1 - zone.WidthPercent); zone.YPercent = Math.Clamp(y, 0, 1 - zone.HeightPercent);
        PositionZones(); if (save) _ = QueueSaveAsync();
    }
    private void Cycle(string id)
    {
        if (_dragZones.FirstOrDefault(z => z.Id == id) is not { } zone) return;
        zone.Action = (DragAction)(((int)zone.Action + 1) % Enum.GetValues<DragAction>().Length);
        PositionZones();
        if (ZoneConfigList.ItemsSource is ZoneRow[] rows && rows.FirstOrDefault(r => r.Id == id) is { } row) row.ActionIndex = (int)zone.Action;
        _ = QueueSaveAsync();
    }
    private void OnConfigChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_applying || !IsLoaded || sender is not ComboBox { DataContext: ZoneRow row } combo || combo.SelectedIndex < 0) return;
        if (_dragZones.FirstOrDefault(z => z.Id == row.Id) is not { } zone || (int)zone.Action == combo.SelectedIndex) return;
        zone.Action = (DragAction)combo.SelectedIndex; PositionZones(); _ = QueueSaveAsync();
    }
    private void OnAddZone(object sender, RoutedEventArgs e)
    { _dragZones.Add(new() { Label = $"区域 {_dragZones.Count + 1}", XPercent = 0.3, YPercent = 0.3 }); Rebuild(); _ = QueueSaveAsync(); }
    private void OnDeleteZone(object sender, RoutedEventArgs e)
    { if (sender is Button { DataContext: ZoneRow row }) DeleteZone(row.Id); }
    private void DeleteZone(string id)
    { if (_dragZones.Count <= 1) return; _dragZones.RemoveAll(z => z.Id == id); Rebuild(); _ = QueueSaveAsync(); }
    private void OnResetDragZones(object sender, RoutedEventArgs e)
    { CancelGesture(); _dragZones = DragZoneConfig.GetDefaults(); Rebuild(); _ = QueueSaveAsync(); }
    private void OnRetrySave(object sender, RoutedEventArgs e) => _ = QueueSaveAsync();
    private Task QueueSaveAsync()
    {
        _hasUnsavedChanges = true;
        var revision = ++_revision; var snapshot = _dragZones.Select(Clone).ToList(); var previous = _save;
        _save = SaveAsync();
        DesktopUiWork.Track(_save);
        return _save;
        async Task SaveAsync()
        {
            await previous;
            if (revision != _revision) return;
            try
            {
                await _store.SaveDragZoneConfigAsync(snapshot);
                if (revision == _revision) _hasUnsavedChanges = false;
                if (IsLoaded && revision == _revision) { ZoneStatus.IsOpen = false; RetrySave.Visibility = Visibility.Collapsed; }
            }
            catch (Exception error) when (error is Microsoft.Data.Sqlite.SqliteException or IOException or InvalidOperationException)
            {
                _logger.LogError(error, "Drag zone settings save failed");
                if (IsLoaded && revision == _revision) { Feedback("设置尚未保存，请重试。", true); RetrySave.Visibility = Visibility.Visible; }
            }
        }
    }
    private static DragZoneConfig Clone(DragZoneConfig zone) => new() { Id = zone.Id, Label = zone.Label, Action = zone.Action,
        XPercent = zone.XPercent, YPercent = zone.YPercent, WidthPercent = zone.WidthPercent, HeightPercent = zone.HeightPercent };
    private static string Label(DragAction action) => action switch
    { DragAction.Watching => "追番", DragAction.PlanToWatch => "补番", DragAction.NotInterested => "不感兴趣",
        DragAction.Following => "关注", DragAction.Completed => "已看完", DragAction.Dropped => "已弃番", DragAction.Blocked => "屏蔽", _ => "禁用" };
    private sealed record Gesture(string Id, DragZoneConfig Original, Point Start, string Edge);
}

internal sealed class ZoneRow(string id, string label, int action) : ObservableObject
{
    public string Id { get; } = id;
    public string Label { get; } = label;
    private int _action = action;
    public int ActionIndex { get => _action; set => SetProperty(ref _action, value); }
}
