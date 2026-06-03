using AniMeido.Contracts;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AniMeido.App.Services;

/// <summary>
/// 导航菜单构建器。从 PluginNavigationItem 列表构建 NavigationView 菜单项。
/// </summary>
internal sealed class NavigationMenuBuilder
{
    /// <summary>构建 NavigationView 的普通菜单项和设置页按钮。先清空旧菜单，避免重复添加。</summary>
    public static void Build(NavigationView naviView, IReadOnlyList<PluginNavigationItem> items)
    {
        naviView.MenuItems.Clear();
        naviView.FooterMenuItems.Clear();

        foreach (var item in items.Where(n => !n.IsSettingsPage))
        {
            var naviItem = new NavigationViewItem
            {
                Content = item.Label,
                Icon = new FontIcon { Glyph = item.Icon, FontSize = 18 },
                Tag = item,
                Margin = new Thickness(0, 6, 0, 6),
                MinHeight = 56,
            };
            naviView.MenuItems.Add(naviItem);
        }

        // 手动添加设置按钮
        var settingsItem = new NavigationViewItem
        {
            Content = "设置",
            Icon = new FontIcon { Glyph = "\uE713", FontSize = 18 },
            Tag = "Settings",
            Margin = new Thickness(0, 6, 0, 6),
            MinHeight = 56,
        };
        naviView.FooterMenuItems.Add(settingsItem);
    }
}
