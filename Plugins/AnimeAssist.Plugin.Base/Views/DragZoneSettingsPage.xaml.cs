using AniMeido.Contracts;
using AniMeido.Plugin.Base.Models;
using AniMeido.Plugin.Base.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Windows.UI;

namespace AniMeido.Plugin.Base.Views
{
    public sealed partial class DragZoneSettingsPage : Page
    {
        private List<DragZoneConfig> _dragZones = DragZoneConfig.GetDefaults();
        private readonly Dictionary<string, ZoneVisual> _zoneVisuals = new();
        private bool _suppressEvents;
        private string? _dragAction; // "move" or "resize"
        private string? _activeZoneId;
        private string? _resizeEdge; // "left"/"right"/"top"/"bottom" or combined
        private double _dragOffsetX, _dragOffsetY;
        private double _dragStartX, _dragStartY, _dragStartW, _dragStartH;
        private bool _previewInitialized;
        private CacheService? _cacheService;

        public DragZoneSettingsPage()
        {
            InitializeComponent();
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            _suppressEvents = true;
            var tracking = AppServices.Provider!.GetRequiredService<TrackingService>();
            _dragZones = await tracking.LoadDragZoneConfigAsync();
            _cacheService = AppServices.Provider!.GetRequiredService<CacheService>();
            RebuildAll();
            _suppressEvents = false;

            _ = UpdateCacheInfoAsync();
            InitDataSettings();
        }

        private async Task UpdateCacheInfoAsync()
        {
            if (_cacheService == null) return;

            var (dbCount, dbSizeKB) = await _cacheService.GetCacheStatsAsync();
            var (imgCount, imgSizeMB) = ImageCacheHelper.GetCacheStats();
            var totalSizeMB = dbSizeKB / 1024.0 + imgSizeMB;
            CacheInfoText.Text = totalSizeMB >= 1.0
                ? $"占用约 {totalSizeMB:F1} MB"
                : $"占用约 {dbSizeKB + imgSizeMB * 1024.0:F0} KB";
        }

        private async void OnClearCacheClick(object sender, RoutedEventArgs e)
        {
            if (_cacheService == null) return;

            // 确认弹窗
            if (ClearCacheButton.XamlRoot is { } xamlRoot)
            {
                var dialog = new ContentDialog
                {
                    Title = "清理缓存",
                    Content = "确定要清空所有本地缓存数据吗？下次访问时将重新从网络获取。",
                    PrimaryButtonText = "确认清理",
                    CloseButtonText = "取消",
                    DefaultButton = ContentDialogButton.Close,
                    XamlRoot = xamlRoot
                };
                var result = await dialog.ShowAsync();
                if (result != ContentDialogResult.Primary) return;
            }

            ClearCacheButton.IsEnabled = false;
            ClearCacheButton.Content = "清理中…";

            try
            {
                await _cacheService.ClearAllCacheAsync();
                ImageCacheHelper.ClearAll();
                await UpdateCacheInfoAsync();
            }
            finally
            {
                ClearCacheButton.IsEnabled = true;
                ClearCacheButton.Content = "清理缓存";
            }
        }

        private void OnPreviewBorderSizeChanged(object sender, SizeChangedEventArgs e)
        {
            // 保持 16:9 比例
            var targetH = e.NewSize.Width * 9.0 / 16.0;
            PreviewBorder.Height = Math.Clamp(targetH, 200, 400);

            if (!_previewInitialized)
            {
                _previewInitialized = true;
                PreviewBorder.SizeChanged -= OnPreviewBorderSizeChanged;

                // 导航到预览页面
                if (PreviewFrame.Content == null)
                    PreviewFrame.Navigate(typeof(DragZonePreviewPage));
            }

            // 更新所有 zone 位置
            PositionAllZones();
        }

        // ======== 重建所有 UI ========

        private void RebuildAll()
        {
            ClearAllZones();
            PopulatePreviewZones();
            PopulateConfigPanel();
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

        // ======== 预览区 Zone 创建 ========

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
            // 标签
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

            // 删除按钮
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

            // 拖拽移动 — zone 本体
            zone.PointerEntered += OnZonePointerEntered;
            zone.PointerExited += OnZonePointerExited;
            zone.PointerPressed += OnZonePointerPressed;
            zone.PointerMoved += OnZonePointerMoved;
            zone.PointerReleased += OnZonePointerReleased;
            zone.PointerCanceled += OnZonePointerCanceled;

            // 点击切换动作
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

        // ======== 配置面板 ========

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

        // ======== 拖拽移动与边缘缩放 (Zone 本体) ========

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
                // 边缘缩放
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
                // 拖拽移动
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

                // 根据边缘计算新位置和尺寸
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

                // 约束最小/最大尺寸
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

        // ======== 光标切换 ========

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
                _ = SaveAsync();
            }
        }

        private void OnZonePointerCanceled(object sender, PointerRoutedEventArgs e)
        {
            _dragAction = null;
            _resizeEdge = null;
            _activeZoneId = null;
            ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.Arrow);
        }

        // ======== 点击切换动作 ========

        private void OnPreviewZoneTapped(object sender, TappedRoutedEventArgs e)
        {
            if (sender is Border zone && zone.Tag is string id)
            {
                var config = _dragZones.Find(z => z.Id == id);
                if (config == null) return;

                // 循环切换动作
                var actions = Enum.GetValues<DragAction>();
                config.Action = (DragAction)(((int)config.Action + 1) % actions.Length);
                UpdateSingleZone(id);

                // 同步 ComboBox
                if (ZoneConfigList.ItemsSource is IList<ZoneConfigItem> items)
                {
                    var item = items.FirstOrDefault(i => i.Id == id);
                    if (item != null)
                        item.ActionIndex = (int)config.Action;
                }
                _ = SaveAsync();
            }
        }

        // ======== 删除区域 ========

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
            _ = SaveAsync();
        }

        // ======== 添加区域 ========

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

            // 添加视觉
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
            _ = SaveAsync();
        }

        // ======== 配置面板事件 ========

        private void OnConfigComboChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressEvents || sender is not ComboBox combo) return;
            var id = combo.Tag as string;
            if (id == null) return;
            var config = _dragZones.Find(z => z.Id == id);
            if (config == null) return;

            config.Action = (DragAction)combo.SelectedIndex;
            UpdateSingleZone(id);
            _ = SaveAsync();
        }

        // ======== 重置 ========

        private async void OnResetDragZones(object sender, RoutedEventArgs e)
        {
            _suppressEvents = true;
            _dragZones = DragZoneConfig.GetDefaults();
            RebuildAll();
            _suppressEvents = false;
            await SaveAsync();
        }

        // ======== 保存 ========

        private async Task SaveAsync()
        {
            if (AppServices.Provider == null) return;
            var tracking = AppServices.Provider.GetRequiredService<TrackingService>();
            await tracking.SaveDragZoneConfigAsync(_dragZones);
        }

        // ======== 辅助方法 ========

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

        // ======== 数据管理 ========

        private ExportService? _exportService;

        private void InitDataSettings()
        {
            _exportService = AppServices.Provider?.GetService<ExportService>();

            DbPathText.Text = AppServices.DatabasePath ?? "（未知）";
            BackupPathText.Text = AppServices.BackupDirectory ?? "（未知）";
            LogPathText.Text = AppServices.LogDirectory ?? "（未知）";
        }

        private void OnOpenDbDirClick(object sender, RoutedEventArgs e)
        {
            var dir = Path.GetDirectoryName(AppServices.DatabasePath);
            if (dir != null)
                _ = Windows.System.Launcher.LaunchFolderPathAsync(dir);
        }

        private void OnOpenBackupDirClick(object sender, RoutedEventArgs e)
        {
            if (AppServices.BackupDirectory != null)
                _ = Windows.System.Launcher.LaunchFolderPathAsync(AppServices.BackupDirectory);
        }

        private void OnOpenLogDirClick(object sender, RoutedEventArgs e)
        {
            if (AppServices.LogDirectory != null)
                _ = Windows.System.Launcher.LaunchFolderPathAsync(AppServices.LogDirectory);
        }

        private async void OnBackupNowClick(object sender, RoutedEventArgs e)
        {
            if (AppServices.BackupDatabaseAsync == null) return;
            BackupNowButton.IsEnabled = false;
            BackupNowButton.Content = "备份中…";
            try
            {
                await AppServices.BackupDatabaseAsync();

                var dialog = new ContentDialog
                {
                    Title = "备份完成",
                    Content = $"数据库已备份到：\n{AppServices.BackupDirectory}",
                    CloseButtonText = "确定",
                    XamlRoot = this.XamlRoot
                };
                await dialog.ShowAsync();
            }
            catch (Exception ex)
            {
                var dialog = new ContentDialog
                {
                    Title = "备份失败",
                    Content = ex.Message,
                    CloseButtonText = "确定",
                    XamlRoot = this.XamlRoot
                };
                await dialog.ShowAsync();
            }
            finally
            {
                BackupNowButton.IsEnabled = true;
                BackupNowButton.Content = "备份";
            }
        }

        private async void OnExportClick(object sender, RoutedEventArgs e)
        {
            if (_exportService == null) return;

            ExportButton.IsEnabled = false;
            ExportButton.Content = "导出中…";
            try
            {
                var json = await _exportService.ExportAsync();

                var picker = new Windows.Storage.Pickers.FileSavePicker();
                picker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary;
                picker.FileTypeChoices.Add("JSON 文件", new[] { ".json" });
                picker.SuggestedFileName = $"AniMeido-{DateTime.Now:yyyyMMdd}.json";

                if (AppServices.MainWindow is Microsoft.UI.Xaml.Window w)
                {
                    var hWnd = WinRT.Interop.WindowNative.GetWindowHandle(w);
                    WinRT.Interop.InitializeWithWindow.Initialize(picker, hWnd);
                }

                var file = await picker.PickSaveFileAsync();
                if (file != null)
                {
                    await Windows.Storage.FileIO.WriteTextAsync(file, json);
                }
            }
            catch (Exception ex)
            {
                var dialog = new ContentDialog
                {
                    Title = "导出失败",
                    Content = ex.Message,
                    CloseButtonText = "确定",
                    XamlRoot = this.XamlRoot
                };
                await dialog.ShowAsync();
            }
            finally
            {
                ExportButton.IsEnabled = true;
                ExportButton.Content = "导出";
            }
        }

        private async void OnImportClick(object sender, RoutedEventArgs e)
        {
            if (_exportService == null) return;

            var picker = new Windows.Storage.Pickers.FileOpenPicker();
            picker.SuggestedStartLocation = Windows.Storage.Pickers.PickerLocationId.DocumentsLibrary;
            picker.FileTypeFilter.Add(".json");

            if (AppServices.MainWindow is Microsoft.UI.Xaml.Window w)
            {
                var hWnd = WinRT.Interop.WindowNative.GetWindowHandle(w);
                WinRT.Interop.InitializeWithWindow.Initialize(picker, hWnd);
            }

            var file = await picker.PickSingleFileAsync();
            if (file == null) return;

            try
            {
                var json = await Windows.Storage.FileIO.ReadTextAsync(file);

                var preview = ExportService.Preview(json);
                if (preview == null)
                {
                    var errDialog = new ContentDialog
                    {
                        Title = "导入失败",
                        Content = "文件格式无效，请选择有效的 AniMeido 导出文件。",
                        CloseButtonText = "确定",
                        XamlRoot = this.XamlRoot
                    };
                    await errDialog.ShowAsync();
                    return;
                }

                var confirmDialog = new ContentDialog
                {
                    Title = "确认导入",
                    Content = $"将导入 {preview.Tracking.Count} 条追番记录"
                             + (preview.DragZones?.Count > 0 ? $" 和 {preview.DragZones.Count} 项拖放配置" : "")
                             + "\n\n导入前会自动备份当前数据库。",
                    PrimaryButtonText = "开始导入",
                    CloseButtonText = "取消",
                    DefaultButton = ContentDialogButton.Primary,
                    XamlRoot = this.XamlRoot
                };

                if (await confirmDialog.ShowAsync() != ContentDialogResult.Primary)
                    return;

                if (AppServices.BackupDatabaseAsync != null)
                    await AppServices.BackupDatabaseAsync();

                var (trackingCount, configCount) = await _exportService.ImportAsync(json);

                var doneDialog = new ContentDialog
                {
                    Title = "导入完成",
                    Content = $"成功导入 {trackingCount} 条追番记录"
                             + (configCount > 0 ? $" 和 {configCount} 项拖放配置" : ""),
                    CloseButtonText = "确定",
                    XamlRoot = this.XamlRoot
                };
                await doneDialog.ShowAsync();
            }
            catch (Exception ex)
            {
                var errDialog = new ContentDialog
                {
                    Title = "导入失败",
                    Content = $"导入时发生错误：{ex.Message}",
                    CloseButtonText = "确定",
                    XamlRoot = this.XamlRoot
                };
                await errDialog.ShowAsync();
            }
        }
    }

    // ======== 预览 Zone 视觉元素 ========

    internal record ZoneVisual(
        Border Overlay,
        TextBlock Label,
        Border DeleteButton);

    // ======== 配置面板数据项 ========

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
