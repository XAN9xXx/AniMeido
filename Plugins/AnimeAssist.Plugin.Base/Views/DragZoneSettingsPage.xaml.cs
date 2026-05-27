using AniMeido.Contracts;
using AniMeido.Plugin.Base.Models;
using AniMeido.Plugin.Base.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;

namespace AniMeido.Plugin.Base.Views
{
    public sealed partial class DragZoneSettingsPage : Page
    {
        private bool _suppressDragEvents = true;
        private string? _draggingPos;

        public DragZoneSettingsPage()
        {
            InitializeComponent();
        }

        protected override async void OnNavigatedTo(NavigationEventArgs e)
        {
            _suppressDragEvents = true;
            await LoadDragZoneConfig();
            _suppressDragEvents = false;
        }

        private async Task LoadDragZoneConfig()
        {
            if (AppServices.Provider == null) return;
            var tracking = AppServices.Provider.GetRequiredService<TrackingService>();
            var zones = await tracking.LoadDragZoneConfigAsync();
            ApplyConfig(zones);
        }

        private async void OnResetDragZones(object sender, RoutedEventArgs e)
        {
            var defaults = DragZoneConfig.GetDefaults();
            ApplyConfig(defaults);
            if (AppServices.Provider == null) return;
            await AppServices.Provider.GetRequiredService<TrackingService>().SaveDragZoneConfigAsync(defaults);
        }

        private void ApplyConfig(List<DragZoneConfig> zones)
        {
            foreach (var z in zones)
            {
                var combo = z.Position switch
                {
                    DragPosition.TopLeft => TopLeftAction,
                    DragPosition.TopRight => TopRightAction,
                    DragPosition.BottomLeft => BottomLeftAction,
                    DragPosition.BottomRight => BottomRightAction,
                    _ => null
                };
                var slider = z.Position switch
                {
                    DragPosition.TopLeft => TopLeftSize,
                    DragPosition.TopRight => TopRightSize,
                    DragPosition.BottomLeft => BottomLeftSize,
                    DragPosition.BottomRight => BottomRightSize,
                    _ => null
                };
                if (combo != null) combo.SelectedIndex = (int)z.Action;
                if (slider != null) slider.Value = z.SizePercent * 100;
            }
            // 延迟到布局完成后更新预览区（ActualWidth 在 OnNavigatedTo 时为 0）
            PreviewBorder.SizeChanged -= OnPreviewBorderSizeChanged;
            PreviewBorder.SizeChanged += OnPreviewBorderSizeChanged;
        }

        private void OnPreviewBorderSizeChanged(object sender, SizeChangedEventArgs e)
        {
            // 保持 16:9 比例
            var targetH = e.NewSize.Width * 9.0 / 16.0;
            PreviewBorder.Height = Math.Clamp(targetH, 200, 400);

            PreviewBorder.SizeChanged -= OnPreviewBorderSizeChanged;

            // 导航到预览页面（只执行一次）
            if (PreviewFrame.Content == null)
                PreviewFrame.Navigate(typeof(DragZonePreviewPage));

            UpdatePreviewZones();
        }

        // ======== 预览区域操作 ========

        private void UpdatePreviewZones()
        {
            var previewWidth = PreviewBorder.ActualWidth;
            var previewHeight = PreviewBorder.ActualHeight;

            SetZone(PreviewTL, PreviewTLText, HandleTL, TopLeftAction, TopLeftSize, previewWidth, previewHeight, DragPosition.TopLeft);
            SetZone(PreviewTR, PreviewTRText, HandleTR, TopRightAction, TopRightSize, previewWidth, previewHeight, DragPosition.TopRight);
            SetZone(PreviewBL, PreviewBLText, HandleBL, BottomLeftAction, BottomLeftSize, previewWidth, previewHeight, DragPosition.BottomLeft);
            SetZone(PreviewBR, PreviewBRText, HandleBR, BottomRightAction, BottomRightSize, previewWidth, previewHeight, DragPosition.BottomRight);
        }

        private void SetZone(Border zone, TextBlock label, Border handle,
            ComboBox combo, Slider slider, double parentW, double parentH, DragPosition pos)
        {
            var sizePercent = slider.Value / 100;
            zone.Width = parentW * sizePercent;
            zone.Height = parentH * sizePercent;

            var action = (DragAction)combo.SelectedIndex;
            label.Text = action switch
            {
                DragAction.Watching => "追番",
                DragAction.PlanToWatch => "补番",
                DragAction.NotInterested => "不感兴趣",
                _ => "禁用"
            };

            zone.Background = action switch
            {
                DragAction.Watching => new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(220, 0x44, 0x88, 0xFF)),
                DragAction.PlanToWatch => new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(220, 0x44, 0xFF, 0x88)),
                DragAction.NotInterested => new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(220, 0xFF, 0x44, 0x44)),
                _ => new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(160, 0x88, 0x88, 0x88)),
            };
        }

        // 点击预览区切换动作
        private void OnPreviewZoneTapped(object sender, TappedRoutedEventArgs e)
        {
            if (sender is Border zone && zone.Tag is string posStr)
            {
                var (combo, _) = GetControlsForPosition(posStr);
                if (combo == null) return;
                combo.SelectedIndex = (combo.SelectedIndex + 1) % combo.Items.Count;
                // drag zone change 事件会同步到预览
            }
        }

        // ======== 拖拽手柄 ========

        private void OnHandlePointerPressed(object sender, PointerRoutedEventArgs e)
        {
            if (sender is FrameworkElement fe)
            {
                _draggingPos = fe.Name switch
                {
                    "HandleTL" => "TopLeft",
                    "HandleTR" => "TopRight",
                    "HandleBL" => "BottomLeft",
                    "HandleBR" => "BottomRight",
                    _ => null
                };
            }
        }

        private void OnHandlePointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (_draggingPos == null) return;
            var (combo, slider) = GetControlsForPosition(_draggingPos);
            if (combo == null || slider == null) return;
            if (combo.SelectedIndex == 0) return; // 禁用状态下不可调大小

            var pt = e.GetCurrentPoint(PreviewBorder);
            var w = pt.Position.X;
            var h = pt.Position.Y;

            var pos = _draggingPos;
            // 根据位置确定参考点
            if (pos == "TopRight" || pos == "BottomRight")
                w = PreviewBorder.ActualWidth - pt.Position.X;
            if (pos == "BottomLeft" || pos == "BottomRight")
                h = PreviewBorder.ActualHeight - pt.Position.Y;

            var wPercent = Math.Clamp(w / PreviewBorder.ActualWidth, 0.1, 0.5);
            var hPercent = Math.Clamp(h / PreviewBorder.ActualHeight, 0.1, 0.5);
            var avg = (wPercent + hPercent) / 2;

            _suppressDragEvents = true;
            slider.Value = Math.Clamp(avg * 100, slider.Minimum, slider.Maximum);
            _suppressDragEvents = false;
        }

        private async void OnHandlePointerReleased(object sender, PointerRoutedEventArgs e)
        {
            if (_draggingPos != null)
            {
                _draggingPos = null;
                await SaveDragZones();
            }
        }

        // ======== 辅助方法 ========

        private (ComboBox? combo, Slider? slider) GetControlsForPosition(string pos)
        {
            return pos switch
            {
                "TopLeft" => (TopLeftAction, TopLeftSize),
                "TopRight" => (TopRightAction, TopRightSize),
                "BottomLeft" => (BottomLeftAction, BottomLeftSize),
                "BottomRight" => (BottomRightAction, BottomRightSize),
                _ => (null, null)
            };
        }

        // ======== 配置变更 ========

        private async void OnDragZoneChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressDragEvents) return;
            UpdatePreviewZones();
            await SaveDragZones();
        }

        private async void OnDragZoneSliderChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            if (_suppressDragEvents) return;
            UpdatePreviewZones();
            await SaveDragZones();
        }

        private async Task SaveDragZones()
        {
            if (AppServices.Provider == null) return;

            var zones = new List<DragZoneConfig>
            {
                new() { Position = DragPosition.TopLeft, Action = (DragAction)TopLeftAction.SelectedIndex, SizePercent = TopLeftSize.Value / 100 },
                new() { Position = DragPosition.TopRight, Action = (DragAction)TopRightAction.SelectedIndex, SizePercent = TopRightSize.Value / 100 },
                new() { Position = DragPosition.BottomLeft, Action = (DragAction)BottomLeftAction.SelectedIndex, SizePercent = BottomLeftSize.Value / 100 },
                new() { Position = DragPosition.BottomRight, Action = (DragAction)BottomRightAction.SelectedIndex, SizePercent = BottomRightSize.Value / 100 },
            };
            var tracking = AppServices.Provider!.GetRequiredService<TrackingService>();
            await tracking.SaveDragZoneConfigAsync(zones);
        }
    }
}
