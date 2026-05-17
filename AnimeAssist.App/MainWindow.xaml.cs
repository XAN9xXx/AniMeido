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

        private async Task ShowPrivacyDialogAsync()
        {
            if (App.PrivacyService.IsAccepted()) return;

            var dialog = new ContentDialog
            {
                Title = "隐私声明",
                Content = "AnimeAssist 仅向 Bangumi API 请求番剧数据，不收集任何个人信息。后续某些插件的功能可能涉及隐私问题，此类插件安装后，将在初次启动时以弹窗提醒。",
                PrimaryButtonText = "同意",
                CloseButtonText = "拒绝",
                XamlRoot = MainNaviView.XamlRoot
            };

            var result = await dialog.ShowAsync();

            if (result == ContentDialogResult.Primary)
                App.PrivacyService.Accept();
            else
                Environment.Exit(0);
        }

        private async void OnMainNaviViewLoaded(object sender, RoutedEventArgs e)
        {
            await ShowPrivacyDialogAsync();
        }

        // 
        private void BuildNavigationMenu()
        {
            foreach (var item in _naviItems)
            {
                var naviItem = new NavigationViewItem()
                {
                    Content = item.Label,
                    Icon = new FontIcon { Glyph = item.Icon },
                    Tag = item.PageTypeName,
                };
                MainNaviView.MenuItems.Add(naviItem);
            }
            MainNaviView.Loaded += OnMainNaviViewLoaded;
        }

        // 
        private void OnNaviItemInvoked(NavigationView sender, NavigationViewItemInvokedEventArgs args)
        {
            if (args.IsSettingsInvoked)
            {
                ContentFrame.Navigate(typeof(Views.SettingPage));
                return;
            }

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
