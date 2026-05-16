using AnimeAssist.Contracts;
using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AnimeAssist.App
{
    public sealed partial class MainWindow : Window
    {
        private readonly IReadOnlyList<PluginNavigationItem> _naviItems;



        public MainWindow(IReadOnlyList<PluginNavigationItem> naviItems)
        {
            InitializeComponent();
            _naviItems = naviItems;
            BuildNavigationMenu();

            if (MainNaviView.MenuItems.Count > 0)
            {
                MainNaviView.SelectedItem = MainNaviView.MenuItems[0];
                NavigateTo(_naviItems[0].PageTypeName);
            }

            MainNaviView.ItemInvoked += OnNaviItemInvoked;
        }

        // 
        private void BuildNavigationMenu()
        {
            foreach (var item in _naviItems)
            {
                var naviItem = new NavigationViewItem()
                {
                    Content = item.Label,
                    Icon = new FontIcon { Glyph = item.Icon, FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Segoe Fluent Icons") },
                    Tag = item.PageTypeName,
                };
                MainNaviView.MenuItems.Add(naviItem);
            }
        }

        // 
        private void OnNaviItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
        {
            var container = args.InvokedItemContainer as NavigationViewItem;
            if (container == null)
                return;

            var pageTypeName = container.Tag as string;
            if (string.IsNullOrEmpty(pageTypeName))
                return;

            NavigateTo(pageTypeName);
        }

        // 
        private void NavigateTo(string pageTypeName)
        {
            var pageType = AppDomain.CurrentDomain.GetAssemblies()
            .Select(a => a.GetType(pageTypeName))
            .FirstOrDefault(t => t != null);
            if (pageType == null)
            {
                Debug.WriteLine($"页面类型未找到: {pageTypeName}");
                return;
            }
            ContentFrame.Navigate(pageType);
        }
    }
}
