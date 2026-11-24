using AniMeido.App.Services;
using AniMeido.PluginProtocol;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;

namespace AniMeido.App.Views;

public sealed partial class SettingPage : Page
{
    private static string _lastSelected = "appearance";
    private readonly PageFactory _pages;
    private readonly PluginContributionRegistry _contributions;
    private readonly Dictionary<string, Page> _cache = new(StringComparer.Ordinal);
    private readonly HashSet<ToggleButton> _buttons = [];
    private string _selected = _lastSelected;
    private IReadOnlyList<DesktopSettingsTarget> _targets = [];
    private bool _attached;
    private readonly AniMeido.Plugin.Base.Views.Controls.DesktopSectionMotion _sectionMotion = new();

    public SettingPage(PageFactory pageFactory, PluginContributionRegistry contributions)
    {
        _pages = pageFactory;
        _contributions = contributions;
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs args)
    {
        if (_attached) return;
        _attached = true;
        _contributions.Changed += OnContributionsChanged;
        RebuildSections();
    }

    private void OnUnloaded(object sender, RoutedEventArgs args)
    {
        _attached = false;
        _contributions.Changed -= OnContributionsChanged;
    }

    private void OnContributionsChanged(object? sender, EventArgs args)
        => DispatcherQueue.TryEnqueue(() =>
        {
            if (!_attached) return;
            foreach (var target in _targets.Where(target => target.Hosted is not null))
                _cache.Remove(target.Id);
            RebuildSections();
        });

    private void RebuildSections()
    {
        var targets = new List<DesktopSettingsTarget>
        {
            new("appearance", "外观", "\uE790", typeof(AppSettingsPage)),
            new("window", "窗口", "\uE737", typeof(AppSettingsPage)),
        };
        if (App.Plugins is { } plugins)
            targets.AddRange(SettingsEntryCollector.Collect(plugins).Select(entry =>
                new DesktopSettingsTarget($"local:{entry.PluginId}:{entry.PageType.FullName}",
                    entry.Label, entry.Icon, entry.PageType)));
        targets.Add(new("plugins", "插件管理", "\uE8D4", typeof(AppSettingsPage)));
        targets.Add(new("about", "关于", "\uE946", typeof(AppSettingsPage)));
        targets.AddRange(_contributions.Settings.Select(entry => new DesktopSettingsTarget(
            $"hosted:{entry.PluginId}:{entry.SettingsId}", entry.Title, entry.Icon, null, entry)));
        _targets = targets;
        SectionItems.ItemsSource = targets;
        foreach (var key in _cache.Keys.Except(targets.Select(target => target.Id)).ToArray())
            _cache.Remove(key);
        Show(targets.FirstOrDefault(target => target.Id == _selected) ?? targets[0]);
    }

    private void OnSectionLoaded(object sender, RoutedEventArgs args)
    {
        var button = (ToggleButton)sender;
        _buttons.Add(button);
        button.IsChecked = button.Tag is DesktopSettingsTarget target && target.Id == _selected;
    }

    private void OnSectionUnloaded(object sender, RoutedEventArgs args) => _buttons.Remove((ToggleButton)sender);

    private void OnSectionClick(object sender, RoutedEventArgs args)
    {
        if (sender is not ToggleButton { Tag: DesktopSettingsTarget target }) return;
        var previous = _selected;
        var backward = _targets.TakeWhile(entry => entry.Id != previous).Any(entry => entry.Id == target.Id);
        Show(target);
        if (previous != target.Id) _sectionMotion.Play(SettingsFrame, backward);
    }

    private void Show(DesktopSettingsTarget target)
    {
        _selected = _lastSelected = target.Id;
        if (!_cache.TryGetValue(target.Id, out var page))
        {
            page = target.PageType is { } pageType ? _pages.CreatePage(pageType)
                : new HostedPluginSettingsPage(_contributions, target.Hosted!);
            if (page is AppSettingsPage settings) settings.SetDesktopSection(target.Id);
            _cache.Add(target.Id, page);
        }
        if (!ReferenceEquals(SettingsFrame.Content, page)) SettingsFrame.Content = page;
        foreach (var button in _buttons)
            button.IsChecked = button.Tag is DesktopSettingsTarget entry && entry.Id == target.Id;
    }
}

public sealed record DesktopSettingsTarget(string Id, string Label, string Icon, Type? PageType,
    HostedSettingsContribution? Hosted = null);
