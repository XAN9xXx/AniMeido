using AniMeido.Contracts;
using AniMeido.Plugin.Base.Models;
using AniMeido.Plugin.Base.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Navigation;

namespace AniMeido.Plugin.Base.Views
{
    public sealed partial class DragZoneSettingsPage : Page
    {
        private bool _suppressDragEvents = true;

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
        }

        private async void OnDragZoneChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_suppressDragEvents) return;
            await SaveDragZones();
        }

        private async void OnDragZoneSliderChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            if (_suppressDragEvents) return;
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
