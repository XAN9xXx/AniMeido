using AniMeido.Contracts;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AniMeido.App.Services;

/// <summary>
/// 导航菜单构建器。从 PluginNavigationItem 列表构建 NavigationView 菜单项。
/// 优先使用 SymbolIcon，退回到 FontIcon。
/// </summary>
internal sealed class NavigationMenuBuilder
{
    /// <summary>将标签文字映射到 WinUI Symbol 枚举值。</summary>
    private static Symbol? ToSymbol(string label)
    {
        return label switch
        {
            "正在放送" => Symbol.Calendar,
            "补番计划" => Symbol.Clock,
            "搜索" => Symbol.Find,
            "关注管理" => Symbol.Favorite,
            "拖放标记" => null, // Symbol.Move 不存在，回退 FontIcon
            "数据管理" => Symbol.Save,
            "设置" => Symbol.Setting,
            _ => null,
        };
    }

    /// <summary>根据 glyph 字符串创建适配的 IconElement。</summary>
    private static IconElement CreateIcon(string label, string glyph, double fontSize = 18)
    {
        var symbol = ToSymbol(label);
        if (symbol.HasValue)
        {
            return new SymbolIcon { Symbol = symbol.Value };
        }
        return new FontIcon { Glyph = glyph, FontSize = fontSize };
    }

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
                Icon = CreateIcon(item.Label, item.Icon),
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
            Icon = CreateIcon("设置", "\uE713"),
            Tag = "Settings",
            Margin = new Thickness(0, 6, 0, 6),
            MinHeight = 56,
        };
        naviView.FooterMenuItems.Add(settingsItem);
    }
}
