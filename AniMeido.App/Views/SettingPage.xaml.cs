using AniMeido.App.Services;
using AniMeido.PluginProtocol;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AniMeido.App.Views;

public sealed partial class SettingPage : Page
{
    private readonly PageFactory _pageFactory;
    private readonly PluginContributionRegistry _contributions;
    private bool _isLoaded;
    private bool _suppressSelectionChanged;
    private string? _currentTargetId;

    public SettingPage(
        PageFactory pageFactory,
        PluginContributionRegistry contributions)
    {
        _pageFactory = pageFactory;
        _contributions = contributions;
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        RebuildNavigation();
    }

    private void OnLoaded(object sender, RoutedEventArgs args)
    {
        if (_isLoaded)
        {
            return;
        }

        _isLoaded = true;
        _contributions.Changed += OnContributionsChanged;
        RebuildNavigation();
    }

    private void OnUnloaded(object sender, RoutedEventArgs args)
    {
        if (!_isLoaded)
        {
            return;
        }

        _isLoaded = false;
        _contributions.Changed -= OnContributionsChanged;
    }

    private void OnContributionsChanged(object? sender, EventArgs args)
        => DispatcherQueue.TryEnqueue(() =>
        {
            if (_isLoaded)
            {
                RebuildNavigation();
            }
        });

    private void RebuildNavigation()
    {
        var selectedId = (SettingsNavigation.SelectedItem
            as NavigationViewItem)?.Tag is SettingsTarget selected
                ? selected.Id
                : AppSettingsId;
        SettingsNavigation.MenuItems.Clear();

#if !UNO_DESKTOP
        SettingsNavigation.MenuItems.Add(
            new NavigationViewItemHeader { Content = "AniMeido" });
#endif
        var appItem = CreateNavigationItem(
#if UNO_DESKTOP
            "外观",
            "\uE790",
#else
            "App 设置",
            "\uE713",
#endif
            new SettingsTarget(
                AppSettingsId,
                typeof(AppSettingsPage),
                null));
        SettingsNavigation.MenuItems.Add(appItem);
#if UNO_DESKTOP
        SettingsNavigation.MenuItems.Add(CreateNavigationItem(
            "窗口", "\uE737", new SettingsTarget("app:window", typeof(AppSettingsPage), null)));
#endif

        if (App.Plugins is not null)
        {
            foreach (var group in SettingsEntryCollector
                .Collect(App.Plugins)
                .GroupBy(entry => new
                {
                    entry.PluginId,
                    entry.PluginDisplayName,
                }))
            {
#if !UNO_DESKTOP
                SettingsNavigation.MenuItems.Add(
                    new NavigationViewItemHeader
                    {
                        Content = group.Key.PluginDisplayName,
                    });
#endif
                foreach (var entry in group)
                {
                    SettingsNavigation.MenuItems.Add(
                        CreateNavigationItem(
                            entry.Label,
                            entry.Icon,
                            new SettingsTarget(
                                $"local:{entry.PluginId}:{entry.PageType.FullName}",
                                entry.PageType,
                                null)));
                }
            }
        }

#if UNO_DESKTOP
        SettingsNavigation.MenuItems.Add(CreateNavigationItem(
            "插件管理", "\uE8D4", new SettingsTarget("app:plugins", typeof(AppSettingsPage), null)));
        SettingsNavigation.MenuItems.Add(CreateNavigationItem(
            "关于", "\uE946", new SettingsTarget("app:about", typeof(AppSettingsPage), null)));
#endif

        foreach (var group in _contributions.Settings.GroupBy(
            entry => new
            {
                entry.PluginId,
                entry.PluginDisplayName,
            }))
        {
#if !UNO_DESKTOP
            SettingsNavigation.MenuItems.Add(
                new NavigationViewItemHeader
                {
                    Content = group.Key.PluginDisplayName,
                });
#endif
            foreach (var entry in group)
            {
                SettingsNavigation.MenuItems.Add(
                    CreateNavigationItem(
                        entry.Title,
                        entry.Icon,
                        new SettingsTarget(
                            $"hosted:{entry.PluginId}:{entry.SettingsId}",
                            null,
                            entry)));
            }
        }

        var selectedItem = SettingsNavigation.MenuItems
            .OfType<NavigationViewItem>()
            .FirstOrDefault(item =>
                item.Tag is SettingsTarget target
                && string.Equals(
                    target.Id,
                    selectedId,
                    StringComparison.Ordinal))
            ?? appItem;
        _suppressSelectionChanged = true;
        SettingsNavigation.SelectedItem = selectedItem;
        _suppressSelectionChanged = false;
        ShowTarget((SettingsTarget)selectedItem.Tag);
    }

    private static NavigationViewItem CreateNavigationItem(
        string label,
        string icon,
        SettingsTarget target)
        => new()
        {
            Content = label,
            Icon = new FontIcon { Glyph = icon },
            Tag = target,
        };

    private void OnSettingsSelectionChanged(
        NavigationView sender,
        NavigationViewSelectionChangedEventArgs args)
    {
        if (!_suppressSelectionChanged
            && args.SelectedItemContainer?.Tag is SettingsTarget target)
        {
            ShowTarget(target);
        }
    }

    private void ShowTarget(SettingsTarget target)
    {
        if (target.PageType is not null
            && string.Equals(
                _currentTargetId,
                target.Id,
                StringComparison.Ordinal)
            && SettingsFrame.Content is not null)
        {
            return;
        }

        _currentTargetId = target.Id;
        if (target.PageType is not null)
        {
            SettingsFrame.Content = _pageFactory.CreatePage(target.PageType);
#if UNO_DESKTOP
            if (SettingsFrame.Content is AppSettingsPage desktopSettings)
            {
                desktopSettings.SetDesktopSection(target.Id switch
                {
                    "app:window" => "window",
                    "app:plugins" => "plugins",
                    "app:about" => "about",
                    _ => "appearance",
                });
            }
#endif
        }
        else if (target.Hosted is not null)
        {
            SettingsFrame.Content = new HostedPluginSettingsPage(
                _contributions,
                target.Hosted);
        }
#if UNO_DESKTOP
        ApplyDesktopSettingsAppearance(SettingsFrame.Content as FrameworkElement);
#endif
    }

    private const string AppSettingsId = "app";

    private sealed record SettingsTarget(
        string Id,
        Type? PageType,
        HostedSettingsContribution? Hosted);
}
