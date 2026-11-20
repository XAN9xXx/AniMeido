using AniMeido.App.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using AniMeido.Plugin.Base.Services;

namespace AniMeido.App.Views;

public sealed partial class AppSettingsPage
{
    private bool _syncingAppearance;
    private bool _appearanceSubscribed;
    private bool _retryingSettingsSave;
    private int _pendingAppearanceChanges;

    internal void SetDesktopSection(string section)
    {
        AppearanceSection.Visibility = section == "appearance" ? Visibility.Visible : Visibility.Collapsed;
        WindowSection.Visibility = section == "window" ? Visibility.Visible : Visibility.Collapsed;
        PluginSection.Visibility = section == "plugins" ? Visibility.Visible : Visibility.Collapsed;
        AboutSection.Visibility = section == "about" ? Visibility.Visible : Visibility.Collapsed;
    }

    private void OnAppearanceLoaded(object sender, RoutedEventArgs args)
    {
        if (!_appearanceSubscribed)
        {
            _themeService.AppearanceChanged += OnAppearanceChanged;
            _themeService.SettingsSaveStateChanged += OnSettingsSaveStateChanged;
            _appearanceSubscribed = true;
        }
        SynchronizeAppearance();
    }

    private void OnAppearanceUnloaded(object sender, RoutedEventArgs args)
    {
        if (_appearanceSubscribed)
        {
            _themeService.AppearanceChanged -= OnAppearanceChanged;
            _themeService.SettingsSaveStateChanged -= OnSettingsSaveStateChanged;
            _appearanceSubscribed = false;
        }
    }

    private void OnAppearanceChanged(object? sender, DesktopAppearance appearance)
    {
        if (_pendingAppearanceChanges == 0)
            SynchronizeAppearance();
        else
            RefreshSettingsSaveFailure();
    }

    private void OnSettingsSaveStateChanged(object? sender, EventArgs args)
    {
        if (IsLoaded)
            RefreshSettingsSaveFailure();
    }

    private void RefreshSettingsSaveFailure()
    {
        var visible = _themeService.HasSettingsSaveFailure
            && _themeService.CurrentAppearance.VisualStyle != DesktopVisualStyle.Paper;
        DesktopSettingsSaveFailure.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        DesktopSettingsSaveFailure.IsOpen = visible;
        DesktopSettingsRetryButton.IsEnabled = !_retryingSettingsSave;
    }

    private void SynchronizeAppearance()
    {
        _syncingAppearance = true;
        try
        {
            var appearance = _themeService.CurrentAppearance;
            ThemeCombo.SelectedIndex = _themeService.GetCurrentTheme() switch
            {
                ElementTheme.Light => 0,
                ElementTheme.Dark => 1,
                _ => 2,
            };
            VisualStyleChoice.SelectedIndex = (int)appearance.VisualStyle;
            FontStyleChoice.SelectedIndex = DesktopAppearanceFont.ToChoiceIndex(appearance.FontStyle);
            CatalogCardsChoice.SelectedIndex = (int)appearance.CatalogCards;
            PlanRemindersToggle.IsOn = appearance.ShowPlanReminders;
        }
        finally
        {
            _syncingAppearance = false;
        }
        RefreshSettingsSaveFailure();
    }

    private async void OnDesktopThemeSelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (_syncingAppearance || !IsLoaded || ThemeCombo.SelectedIndex < 0)
            return;
        var theme = ThemeCombo.SelectedIndex switch
        {
            0 => ElementTheme.Light,
            1 => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };
        await Task.Yield();
        if (!IsLoaded)
            return;
        await ChangeAppearanceAsync(() => _themeService.SetThemeAsync(theme));
    }

    private async void OnRetryDesktopSettingsSave(object sender, RoutedEventArgs args)
    {
        if (_retryingSettingsSave || !IsLoaded)
            return;
        _retryingSettingsSave = true;
        DesktopSettingsRetryButton.IsEnabled = false;
        try
        {
            await _themeService.RetrySettingsSaveAsync();
        }
        finally
        {
            _retryingSettingsSave = false;
            if (IsLoaded)
                SynchronizeAppearance();
        }
    }

    private async void OnVisualStyleChanged(object sender, SelectionChangedEventArgs args)
    {
        if (_syncingAppearance || !IsLoaded || VisualStyleChoice.SelectedIndex < 0)
            return;
        var choice = (DesktopVisualStyle)VisualStyleChoice.SelectedIndex;
        // Let the native popup finish closing before its theme or template changes.
        await Task.Yield();
        if (!IsLoaded)
            return;
        await ChangeAppearanceAsync(() => _themeService.SetVisualStyleAsync(choice));
    }

    private async void OnFontStyleChanged(object sender, SelectionChangedEventArgs args)
    {
        if (_syncingAppearance || !IsLoaded || FontStyleChoice.SelectedIndex < 0)
            return;
        var choice = DesktopAppearanceFont.FromChoiceIndex(FontStyleChoice.SelectedIndex);
        await Task.Yield();
        if (!IsLoaded)
            return;
        await ChangeAppearanceAsync(() => _themeService.SetFontStyleAsync(choice));
    }

    private async void OnCatalogCardsChanged(object sender, SelectionChangedEventArgs args)
    {
        if (_syncingAppearance || !IsLoaded || CatalogCardsChoice.SelectedIndex < 0)
            return;
        var choice = (DesktopCatalogCardStyle)CatalogCardsChoice.SelectedIndex;
        await Task.Yield();
        if (!IsLoaded)
            return;
        await ChangeAppearanceAsync(() => _themeService.SetCatalogCardStyleAsync(choice));
    }

    private async void OnPlanRemindersChanged(object sender, RoutedEventArgs args)
    {
        if (_syncingAppearance || !IsLoaded)
            return;
        var visible = PlanRemindersToggle.IsOn;
        await ChangeAppearanceAsync(() => _themeService.SetPlanRemindersAsync(visible));
    }

    private async Task ChangeAppearanceAsync(Func<Task> change)
    {
        ++_pendingAppearanceChanges;
        try
        {
            var task = change();
            DesktopUiWork.Track(task);
            await task;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException
            or InvalidOperationException)
        {
            Serilog.Log.Error(ex, "保存桌面外观设置失败");
        }
        finally
        {
            --_pendingAppearanceChanges;
            if (_pendingAppearanceChanges == 0 && IsLoaded)
                SynchronizeAppearance();
        }
    }
}
