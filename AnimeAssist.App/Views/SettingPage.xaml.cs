using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using AniMeido.App.Services; // PluginSettingsEntry, SettingsEntryCollector

namespace AniMeido.App.Views
{
    public sealed partial class SettingPage : Page
    {
        private readonly PageFactory _pageFactory;
        // 记录所有插件导航项的可视元素，用于切换选中态
        private readonly List<PluginNavVisual> _pluginNavs = new();

        public SettingPage(PageFactory pageFactory)
        {
            _pageFactory = pageFactory;
            InitializeComponent();

            // 填充插件设置导航项，按插件分组显示
            if (App.Plugins is not null && App.Plugins.Count > 0)
            {
                var entries = SettingsEntryCollector.Collect(App.Plugins);

                string? lastPluginId = null;
                foreach (var entry in entries)
                {
                    if (lastPluginId != entry.PluginId)
                    {
                        AddPluginGroupHeader(entry.PluginDisplayName);
                        lastPluginId = entry.PluginId;
                    }
                    AddPluginNavItem(entry);
                }
            }

            // 默认选中 App 设置
            SettingsFrame.Content = _pageFactory.CreatePage(typeof(AppSettingsPage));
        }

        /// <summary>添加插件分组标题。</summary>
        private void AddPluginGroupHeader(string pluginName)
        {
            var header = new TextBlock
            {
                Text = pluginName,
                FontSize = 12,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(153, 233, 233, 233)), // 60% opacity
                Margin = new Thickness(12, 16, 0, 4),
                VerticalAlignment = VerticalAlignment.Center,
            };
            PluginSettingsList.Children.Add(header);
        }

        private void AddPluginNavItem(PluginSettingsEntry entry)
        {
            var rect = new Rectangle
            {
                Width = 3,
                Height = 20,
                Fill = new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0)),
                VerticalAlignment = VerticalAlignment.Center
            };
            var label = new TextBlock
            {
                Text = entry.Label,
                FontSize = 14,
                Foreground = (Brush)Resources["NavTextDefaultBrush"],
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(12, 0, 0, 0)
            };
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(3) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.Children.Add(rect);
            Grid.SetColumn(label, 1);
            grid.Children.Add(label);

            var border = new Border
            {
                Padding = new Thickness(12, 10, 12, 10),
                Height = 40,
                Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0)),
                Child = grid,
                Tag = entry
            };
            border.Tapped += OnPluginNavItemTapped;
            border.PointerEntered += OnCardPointerEntered;
            border.PointerExited += OnCardPointerExited;

            PluginSettingsList.Children.Add(border);
            _pluginNavs.Add(new PluginNavVisual(border, rect, label));
        }

        private void SelectAppSettings()
        {
            var accentColor = (Windows.UI.Color)Application.Current.Resources["SystemAccentColor"];
            AppSettingsCard.Background = new SolidColorBrush(
                Windows.UI.Color.FromArgb(20, accentColor.R, accentColor.G, accentColor.B));
            AppSettingsIndicator.Visibility = Visibility.Visible;
            AppSettingsIndicator.Fill = (Brush)Resources["NavIndicatorBrush"];
            AppSettingsLabel.Foreground = (Brush)Resources["NavTextSelectedBrush"];

            // 取消所有插件项的选中态
            foreach (var nav in _pluginNavs)
            {
                nav.Indicator.Fill = new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0));
                nav.Indicator.Visibility = Visibility.Collapsed;
                nav.Label.Foreground = (Brush)Resources["NavTextDefaultBrush"];
            }
        }

        private void OnNavItemTapped(object sender, TappedRoutedEventArgs e)
        {
            var page = _pageFactory.CreatePage(typeof(AppSettingsPage));
            SettingsFrame.Content = page;
            SelectAppSettings();
        }

        private void OnPluginNavItemTapped(object sender, TappedRoutedEventArgs e)
        {
            if (sender is Border border && border.Tag is PluginSettingsEntry entry)
            {
                var page = _pageFactory.CreatePage(entry.PageType);
                SettingsFrame.Content = page;

                // 取消 App 设置选中态
                AppSettingsCard.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0));
                AppSettingsIndicator.Visibility = Visibility.Collapsed;
                AppSettingsLabel.Foreground = (Brush)Resources["NavTextDefaultBrush"];

                // 高亮当前插件
                var accentColor = (Windows.UI.Color)Application.Current.Resources["SystemAccentColor"];
                foreach (var nav in _pluginNavs)
                {
                    if (nav.Border == border)
                    {
                        nav.Indicator.Fill = (Brush)Resources["NavIndicatorBrush"];
                        nav.Indicator.Visibility = Visibility.Visible;
                        nav.Label.Foreground = (Brush)Resources["NavTextSelectedBrush"];
                    }
                    else
                    {
                        nav.Indicator.Fill = new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0));
                        nav.Indicator.Visibility = Visibility.Collapsed;
                        nav.Label.Foreground = (Brush)Resources["NavTextDefaultBrush"];
                    }
                }
            }
        }

        private void OnCardPointerEntered(object sender, PointerRoutedEventArgs e)
        {
            if (sender is Border border)
            {
                var accentColor = (Windows.UI.Color)Application.Current.Resources["SystemAccentColor"];
                border.Background = new SolidColorBrush(
                    Windows.UI.Color.FromArgb(20, accentColor.R, accentColor.G, accentColor.B));
            }
        }

        private void OnCardPointerExited(object sender, PointerRoutedEventArgs e)
        {
            if (sender is Border border)
            {
                bool isAppSettingsSelected = AppSettingsIndicator.Visibility == Visibility.Visible;
                if (border == AppSettingsCard && isAppSettingsSelected)
                    return;

                border.Background = new SolidColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0));
            }
        }

        // 插件设置入口的内部模型（使用 AniMeido.App.Services.PluginSettingsEntry）
        private record PluginNavVisual(Border Border, Rectangle Indicator, TextBlock Label);
    }
}