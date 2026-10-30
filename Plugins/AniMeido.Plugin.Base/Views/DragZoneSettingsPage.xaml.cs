using AniMeido.Plugin.Base.Models;
using AniMeido.Plugin.Base.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace AniMeido.Plugin.Base.Views
{
    public sealed partial class DragZoneSettingsPage : Page
    {
        private readonly PersonalFeatureStateStore _featureState;
        private readonly ILogger<DragZoneSettingsPage> _logger;
        private List<DragZoneConfig> _dragZones = DragZoneConfig.GetDefaults();
        private readonly Dictionary<string, ZoneVisual> _zoneVisuals = new();
        private bool _suppressEvents;
        private string? _dragAction;
        private string? _activeZoneId;
        private string? _resizeEdge;
        private readonly SemaphoreSlim _saveGate = new(1, 1);
        private int _changeVersion;
        private int _saveVersion;
        private double _dragOffsetX, _dragOffsetY;
        private double _dragStartX, _dragStartY, _dragStartW, _dragStartH;



        public DragZoneSettingsPage(
            PersonalFeatureStateStore featureState,
            ILogger<DragZoneSettingsPage> logger)
        {
            _featureState = featureState;
            _logger = logger;
            InitializeComponent();
            _ = LoadAsync();
        }

        private async Task LoadAsync()
        {
            var changeVersion = _changeVersion;
            try
            {
                var dragZones = await _featureState.LoadDragZoneConfigAsync();
                if (changeVersion != _changeVersion)
                {
                    return;
                }
                _dragZones = dragZones;
                _suppressEvents = true;
                try
                {
                    RebuildAll();
                }
                finally
                {
                    _suppressEvents = false;
                }
            }
#pragma warning disable CA1031 // 拖放配置加载失败不阻止页面显示
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "拖放标记设置加载失败，继续显示当前内容。");
            }
#pragma warning restore CA1031
        }

        private void OnPreviewBorderSizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (!_zonesReady)
            {
                _zonesReady = true;

                if (PreviewFrame.Content == null)
                    PreviewFrame.Navigate(typeof(DragZonePreviewPage));

                PopulatePreviewZones();
            }

            var targetH = e.NewSize.Width * 9.0 / 16.0;
            PreviewBorder.Height = Math.Clamp(targetH, 200, 400);

            PositionAllZones();
        }

        private bool _zonesReady;

        private void RebuildAll()
        {
            ClearAllZones();
            PopulateConfigPanel();
            // 预览区 Zone 仅在布局就绪后添加，否则等待 SizeChanged
            if (_zonesReady)
                PopulatePreviewZones();
        }

        private void ClearAllZones()
        {
            var grid = PreviewBorder.Child as Grid;
            if (grid == null) return;

            foreach (var kv in _zoneVisuals)
                grid.Children.Remove(kv.Value.Overlay);
            _zoneVisuals.Clear();
            ZoneConfigList.ItemsSource = null;
        }

        private void PopulatePreviewZones()
        {
            var grid = PreviewBorder.Child as Grid;
            if (grid == null) return;

            foreach (var config in _dragZones)
            {
                var visual = CreateZoneVisual(config);
                _zoneVisuals[config.Id] = visual;
                grid.Children.Add(visual.Overlay);
            }
            PositionAllZones();
        }

        private ZoneVisual CreateZoneVisual(DragZoneConfig config)
        {
            var label = new TextBlock
            {
                Text = GetActionLabel(config.Action),
                Foreground = new SolidColorBrush(Color.FromArgb(255, 255, 255, 255)),
                FontSize = 13,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Margin = new Thickness(8, 6, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
            };

            var deleteBtn = new Border
            {
                Width = 18,
                Height = 18,
                Background = new SolidColorBrush(Color.FromArgb(180, 0, 0, 0)),
                CornerRadius = new CornerRadius(9),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 4, 4, 0),
                Tag = config.Id,
                IsHitTestVisible = true,
            };
            var deleteText = new TextBlock
            {
                Text = "×",
                Foreground = new SolidColorBrush(Color.FromArgb(255, 255, 255, 255)),
                FontSize = 12,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            deleteBtn.Child = deleteText;
            deleteBtn.Tapped += OnDeleteZoneTapped;

            var innerGrid = new Grid();
            innerGrid.Children.Add(label);
            innerGrid.Children.Add(deleteBtn);

            var zone = new Border
            {
                Child = innerGrid,
                CornerRadius = new CornerRadius(8),
                Background = GetZoneColor(config.Action),
                HorizontalAlignment = HorizontalAlignment.Left,
                VerticalAlignment = VerticalAlignment.Top,
                Tag = config.Id,
                IsHitTestVisible = true,
            };

            zone.PointerEntered += OnZonePointerEntered;
            zone.PointerExited += OnZonePointerExited;
            zone.PointerPressed += OnZonePointerPressed;
            zone.PointerMoved += OnZonePointerMoved;
            zone.PointerReleased += OnZonePointerReleased;
            zone.PointerCanceled += OnZonePointerCanceled;

            zone.Tapped += OnPreviewZoneTapped;

            return new ZoneVisual(zone, label, deleteBtn);
        }

        private void PositionAllZones()
        {
            var pw = PreviewBorder.ActualWidth;
            var ph = PreviewBorder.ActualHeight;
            if (pw <= 0 || ph <= 0) return;

            foreach (var config in _dragZones)
            {
                if (_zoneVisuals.TryGetValue(config.Id, out var vis))
                {
                    vis.Overlay.Width = pw * config.WidthPercent;
                    vis.Overlay.Height = ph * config.HeightPercent;
                    vis.Overlay.Margin = new Thickness(pw * config.XPercent, ph * config.YPercent, 0, 0);
                    vis.Overlay.Background = GetZoneColor(config.Action);
                    vis.Label.Text = GetActionLabel(config.Action);
                }
            }
        }

        private void UpdateSingleZone(string id)
        {
            var config = _dragZones.Find(z => z.Id == id);
            if (config == null) return;
            if (!_zoneVisuals.TryGetValue(id, out var vis)) return;

            var pw = PreviewBorder.ActualWidth;
            var ph = PreviewBorder.ActualHeight;
            if (pw > 0 && ph > 0)
            {
                vis.Overlay.Width = pw * config.WidthPercent;
                vis.Overlay.Height = ph * config.HeightPercent;
                vis.Overlay.Margin = new Thickness(pw * config.XPercent, ph * config.YPercent, 0, 0);
            }
            vis.Overlay.Background = GetZoneColor(config.Action);
            vis.Label.Text = GetActionLabel(config.Action);
        }

        private void PopulateConfigPanel()
        {
            var items = _dragZones.Select(z => new ZoneConfigItem
            {
                Id = z.Id,
                Label = z.Label,
                ActionIndex = (int)z.Action,
                SizeValue = z.WidthPercent * 100,
            }).ToList();
            ZoneConfigList.ItemsSource = items;
        }

        private const double EdgeThreshold = 10; // 边缘检测像素阈值

        private string? DetectEdge(Border zone, Microsoft.UI.Input.PointerPoint pt)
        {
            var w = zone.ActualWidth;
            var h = zone.ActualHeight;
            var x = pt.Position.X;
            var y = pt.Position.Y;

            bool nearLeft = x <= EdgeThreshold;
            bool nearRight = x >= w - EdgeThreshold;
            bool nearTop = y <= EdgeThreshold;
            bool nearBottom = y >= h - EdgeThreshold;

            if (nearLeft && nearTop) return "top-left";
            if (nearRight && nearTop) return "top-right";
            if (nearLeft && nearBottom) return "bottom-left";
            if (nearRight && nearBottom) return "bottom-right";
            if (nearLeft) return "left";
            if (nearRight) return "right";
            if (nearTop) return "top";
            if (nearBottom) return "bottom";
            return null;
        }

        private void OnZonePointerPressed(object sender, PointerRoutedEventArgs e)
        {
            if (sender is not Border zone || zone.Tag is not string id) return;
            var pt = e.GetCurrentPoint(zone);
            var edge = DetectEdge(zone, pt);
            var config = _dragZones.Find(z => z.Id == id);
            if (config == null) return;

            if (edge != null)
            {
                _dragAction = "resize";
                _resizeEdge = edge;
                _activeZoneId = id;
                _dragStartX = config.XPercent;
                _dragStartY = config.YPercent;
                _dragStartW = config.WidthPercent;
                _dragStartH = config.HeightPercent;
                _dragOffsetX = pt.Position.X / zone.ActualWidth;
                _dragOffsetY = pt.Position.Y / zone.ActualHeight;
            }
            else
            {
                _dragAction = "move";
                _activeZoneId = id;
                var ptRel = e.GetCurrentPoint(PreviewBorder);
                _dragOffsetX = ptRel.Position.X - config.XPercent * PreviewBorder.ActualWidth;
                _dragOffsetY = ptRel.Position.Y - config.YPercent * PreviewBorder.ActualHeight;
            }
            zone.CapturePointer(e.Pointer);
        }

        private void OnZonePointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (sender is not Border zone) return;
            var pt = e.GetCurrentPoint(zone);

            if (_dragAction == "move" && _activeZoneId != null)
            {
                var ptRel = e.GetCurrentPoint(PreviewBorder);
                var config = _dragZones.Find(z => z.Id == _activeZoneId);
                if (config == null) return;
                var pw = PreviewBorder.ActualWidth;
                var ph = PreviewBorder.ActualHeight;
                if (pw <= 0 || ph <= 0) return;

                var newX = Math.Clamp((ptRel.Position.X - _dragOffsetX) / pw, 0, 1 - config.WidthPercent);
                var newY = Math.Clamp((ptRel.Position.Y - _dragOffsetY) / ph, 0, 1 - config.HeightPercent);
                config.XPercent = newX;
                config.YPercent = newY;

                if (_zoneVisuals.TryGetValue(_activeZoneId, out var vis))
                    vis.Overlay.Margin = new Thickness(newX * pw, newY * ph, 0, 0);
                return;
            }

            if (_dragAction == "resize" && _activeZoneId != null && _resizeEdge != null)
            {
                var config = _dragZones.Find(z => z.Id == _activeZoneId);
                if (config == null) return;
                var pw = PreviewBorder.ActualWidth;
                var ph = PreviewBorder.ActualHeight;
                if (pw <= 0 || ph <= 0) return;

                var ptRel = e.GetCurrentPoint(PreviewBorder);
                var px = ptRel.Position.X / pw; // 相对预览区百分比
                var py = ptRel.Position.Y / ph;

                double newX = _dragStartX, newY = _dragStartY;
                double newW = _dragStartW, newH = _dragStartH;

                if (_resizeEdge.Contains("left"))
                {
                    newW = _dragStartX + _dragStartW - px;
                    newX = px;
                }
                else if (_resizeEdge.Contains("right"))
                {
                    newW = px - _dragStartX;
                }

                if (_resizeEdge.Contains("top"))
                {
                    newH = _dragStartY + _dragStartH - py;
                    newY = py;
                }
                else if (_resizeEdge.Contains("bottom"))
                {
                    newH = py - _dragStartY;
                }

                newW = Math.Clamp(newW, 0.08, 0.6);
                newH = Math.Clamp(newH, 0.08, 0.6);
                newX = Math.Clamp(newX, 0, 1 - newW);
                newY = Math.Clamp(newY, 0, 1 - newH);

                config.XPercent = newX;
                config.YPercent = newY;
                config.WidthPercent = newW;
                config.HeightPercent = newH;

                if (_zoneVisuals.TryGetValue(_activeZoneId, out var vis))
                {
                    vis.Overlay.Width = pw * newW;
                    vis.Overlay.Height = ph * newH;
                    vis.Overlay.Margin = new Thickness(newX * pw, newY * ph, 0, 0);
                }


                return;
            }
        }

        private void OnZonePointerEntered(object sender, PointerRoutedEventArgs e)
        {
            if (sender is not Border zone) return;
            var pt = e.GetCurrentPoint(zone);
            var edge = DetectEdge(zone, pt);
            ProtectedCursor = edge switch
            {
                "left" or "right" => InputSystemCursor.Create(InputSystemCursorShape.SizeWestEast),
                "top" or "bottom" => InputSystemCursor.Create(InputSystemCursorShape.SizeNorthSouth),
                "top-left" or "bottom-right" => InputSystemCursor.Create(InputSystemCursorShape.SizeNorthwestSoutheast),
                "top-right" or "bottom-left" => InputSystemCursor.Create(InputSystemCursorShape.SizeNortheastSouthwest),
                _ => InputSystemCursor.Create(InputSystemCursorShape.Arrow),
            };
        }

        private void OnZonePointerExited(object sender, PointerRoutedEventArgs e)
        {
            ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.Arrow);
        }

        private void OnZonePointerReleased(object sender, PointerRoutedEventArgs e)
        {
            if (_activeZoneId != null)
            {
                _dragAction = null;
                _resizeEdge = null;
                _activeZoneId = null;
                ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.Arrow);
                _ = QueueSaveAsync();
            }
        }

        private void OnZonePointerCanceled(object sender, PointerRoutedEventArgs e)
        {
            _dragAction = null;
            _resizeEdge = null;
            _activeZoneId = null;
            ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.Arrow);
        }

        private void OnPreviewZoneTapped(object sender, TappedRoutedEventArgs e)
        {
            if (sender is Border zone && zone.Tag is string id)
            {
                var config = _dragZones.Find(z => z.Id == id);
                if (config == null) return;

                var actions = Enum.GetValues<DragAction>();
                config.Action = (DragAction)(((int)config.Action + 1) % actions.Length);
                UpdateSingleZone(id);

                if (ZoneConfigList.ItemsSource is IList<ZoneConfigItem> items)
                {
                    var item = items.FirstOrDefault(i => i.Id == id);
                    if (item != null)
                        item.ActionIndex = (int)config.Action;
                }
                _ = QueueSaveAsync();
            }
        }

        private void OnDeleteZoneTapped(object sender, TappedRoutedEventArgs e)
        {
            if (sender is Border btn && btn.Tag is string id)
                DeleteZone(id);
        }

        private void OnDeleteZone(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string id)
                DeleteZone(id);
        }

        private void DeleteZone(string id)
        {
            if (_dragZones.Count <= 1) return; // 至少保留一个

            _dragZones.RemoveAll(z => z.Id == id);
            if (_zoneVisuals.TryGetValue(id, out var vis))
            {
                var grid = PreviewBorder.Child as Grid;
                grid?.Children.Remove(vis.Overlay);
                _zoneVisuals.Remove(id);
            }
            PopulateConfigPanel();
            _ = QueueSaveAsync();
        }

        private void OnAddZone(object sender, RoutedEventArgs e)
        {
            var pw = PreviewBorder.ActualWidth;
            var ph = PreviewBorder.ActualHeight;

            var newZone = new DragZoneConfig
            {
                Id = Guid.NewGuid().ToString("N")[..8],
                Label = $"区域 {_dragZones.Count + 1}",
                XPercent = 0.3,
                YPercent = 0.3,
                WidthPercent = 0.25,
                HeightPercent = 0.25,
                Action = DragAction.None,
            };
            _dragZones.Add(newZone);

            var grid = PreviewBorder.Child as Grid;
            if (grid != null)
            {
                var visual = CreateZoneVisual(newZone);
                _zoneVisuals[newZone.Id] = visual;
                grid.Children.Add(visual.Overlay);

                if (pw > 0 && ph > 0)
                {
                    visual.Overlay.Width = pw * newZone.WidthPercent;
                    visual.Overlay.Height = ph * newZone.HeightPercent;
                    visual.Overlay.Margin = new Thickness(pw * newZone.XPercent, ph * newZone.YPercent, 0, 0);
                }
            }

            PopulateConfigPanel();
            _ = QueueSaveAsync();
        }

        private void OnConfigComboChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressEvents || sender is not ComboBox combo) return;
            var id = combo.Tag as string;
            if (id == null) return;
            var config = _dragZones.Find(z => z.Id == id);
            if (config == null) return;

            config.Action = (DragAction)combo.SelectedIndex;
            UpdateSingleZone(id);
            _ = QueueSaveAsync();
        }

        private async void OnResetDragZones(object sender, RoutedEventArgs e)
        {
            _suppressEvents = true;
            _dragZones = DragZoneConfig.GetDefaults();
            RebuildAll();
            _suppressEvents = false;
            await QueueSaveAsync();
        }

        private async Task QueueSaveAsync()
        {
            Interlocked.Increment(ref _changeVersion);
            var saveVersion = Interlocked.Increment(ref _saveVersion);
            var snapshot = _dragZones.Select(CloneConfig).ToList();
            await _saveGate.WaitAsync();
            try
            {
                if (saveVersion != _saveVersion)
                {
                    return;
                }
                await _featureState.SaveDragZoneConfigAsync(snapshot);
            }
#pragma warning disable CA1031 // 拖放配置保存失败不阻塞操作
            catch (Exception ex)
            {
                _logger.LogError(ex, "拖放标记设置保存失败。");
            }
#pragma warning restore CA1031
            finally
            {
                _saveGate.Release();
            }
        }

        private static DragZoneConfig CloneConfig(DragZoneConfig config)
            => new()
            {
                Id = config.Id,
                Label = config.Label,
                XPercent = config.XPercent,
                YPercent = config.YPercent,
                WidthPercent = config.WidthPercent,
                HeightPercent = config.HeightPercent,
                Action = config.Action,
            };

        private static string GetActionLabel(DragAction action) => action switch
        {
            DragAction.Watching => "追番",
            DragAction.PlanToWatch => "补番",
            DragAction.NotInterested => "不感兴趣",
            DragAction.Following => "关注",
            DragAction.Completed => "已看完",
            DragAction.Dropped => "已弃番",
            DragAction.Blocked => "屏蔽",
            _ => "禁用"
        };

        private static SolidColorBrush GetZoneColor(DragAction action) => action switch
        {
            DragAction.Watching => new SolidColorBrush(Color.FromArgb(220, 0x44, 0x88, 0xFF)),
            DragAction.PlanToWatch => new SolidColorBrush(Color.FromArgb(220, 0x44, 0xFF, 0x88)),
            DragAction.NotInterested => new SolidColorBrush(Color.FromArgb(220, 0xFF, 0x44, 0x44)),
            DragAction.Following => new SolidColorBrush(Color.FromArgb(220, 0xFF, 0xAA, 0x00)),
            DragAction.Completed => new SolidColorBrush(Color.FromArgb(220, 0x88, 0x44, 0xFF)),
            DragAction.Dropped => new SolidColorBrush(Color.FromArgb(220, 0x88, 0x88, 0x88)),
            DragAction.Blocked => new SolidColorBrush(Color.FromArgb(220, 0x44, 0x44, 0x44)),
            _ => new SolidColorBrush(Color.FromArgb(160, 0x88, 0x88, 0x88)),
        };

    }

    internal record ZoneVisual(
        Border Overlay,
        TextBlock Label,
        Border DeleteButton);

    internal class ZoneConfigItem : ObservableObject
    {
        private string _id = "";
        public string Id
        {
            get => _id;
            set => SetProperty(ref _id, value);
        }

        private string _label = "";
        public string Label
        {
            get => _label;
            set => SetProperty(ref _label, value);
        }

        private int _actionIndex;
        public int ActionIndex
        {
            get => _actionIndex;
            set => SetProperty(ref _actionIndex, value);
        }

        private double _sizeValue = 25;
        public double SizeValue
        {
            get => _sizeValue;
            set => SetProperty(ref _sizeValue, value);
        }
    }
}
