using AniMeido.Plugin.Base.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Text;
using Windows.UI;
using Windows.UI.ViewManagement;

namespace AniMeido.App.Services;

public sealed class ThemeService : IAsyncDisposable
{
    private readonly DesktopSettingsStore _settings;
    private readonly SemaphoreSlim _changes = new(1, 1);
    private readonly ResourceDictionary _resources;
    private readonly Style _nativeToggleSwitchStyle;
    private readonly UISettings _animationSettings = new();
    private bool _systemAnimationsEnabled;
    private DesktopAppearanceEditor _appearance;
    private FrameworkElement? _root;
    private DesktopToastPresenter? _toasts;
    private ElementTheme _theme = ElementTheme.Default;
    private ElementTheme _savedTheme = ElementTheme.Default;
    private bool _settingsSaveFailed;
    private const string SettingsSaveFailureMessage = "更改已生效，设置未能保存。";
    private Task? _disposeTask;
    private bool _applying;
    private bool? _appliedDark;

    public ThemeService(DesktopSettingsStore settings)
    {
        _settings = settings;
        _appearance = CreateEditor(new());
        var definitions = Application.Current.Resources.MergedDictionaries
            .FirstOrDefault(dictionary => dictionary.ContainsKey("DesktopPageBrush"))
            ?? new Themes.DesktopThemeResources();
        _resources = new ResourceDictionary();
        foreach (var entry in definitions)
            _resources[entry.Key] = entry.Value;
        _nativeToggleSwitchStyle = (Style)_resources["DesktopToggleSwitchStyle"];
        Application.Current.Resources.MergedDictionaries.Add(_resources);
        foreach (var (key, source) in new[]
        {
            ("ApplicationPageBackgroundThemeBrush", "DesktopPageBrush"),
            ("TextFillColorPrimaryBrush", "DesktopTextBrush"),
            ("TextFillColorSecondaryBrush", "DesktopMutedBrush"),
            ("TextFillColorTertiaryBrush", "DesktopMutedBrush"),
            ("ContentDialogForeground", "DesktopTextBrush"),
            ("ContentDialogBackground", "DesktopDialogFooterBrush"),
            ("ContentDialogBorderBrush", "DesktopDialogEdgeBrush"),
            ("ContentDialogTopOverlay", "DesktopDialogBodyBrush"),
            ("ContentDialogSeparatorBorderBrush", "DesktopLineBrush"),
            ("AccentFillColorDefaultBrush", "DesktopAccentBrush"),
            ("ButtonBackground", "DesktopButtonBrush"),
            ("ButtonBackgroundPointerOver", "DesktopButtonHoverBrush"),
            ("ButtonBackgroundPressed", "DesktopButtonPressedBrush"),
            ("ButtonForeground", "DesktopTextBrush"),
            ("ButtonForegroundPointerOver", "DesktopTextBrush"),
            ("ButtonForegroundPressed", "DesktopTextBrush"),
            ("ButtonForegroundDisabled", "DesktopMutedBrush"),
            ("ButtonBorderBrush", "DesktopButtonEdgeBrush"),
            ("ButtonBorderBrushPointerOver", "DesktopButtonHoverEdgeBrush"),
            ("ButtonBorderBrushPressed", "DesktopButtonPressedEdgeBrush"),
            ("ToggleButtonBackground", "DesktopButtonBrush"),
            ("ToggleButtonBackgroundPointerOver", "DesktopButtonHoverBrush"),
            ("ToggleButtonBackgroundPressed", "DesktopButtonPressedBrush"),
            ("ToggleButtonForeground", "DesktopTextBrush"),
            ("ToggleButtonForegroundPointerOver", "DesktopTextBrush"),
            ("ToggleButtonForegroundPressed", "DesktopTextBrush"),
            ("ToggleButtonBorderBrush", "DesktopButtonEdgeBrush"),
            ("ToggleButtonBorderBrushPointerOver", "DesktopButtonHoverEdgeBrush"),
            ("ToggleButtonBorderBrushPressed", "DesktopButtonPressedEdgeBrush"),
            ("AniMeidoPageBackgroundBrush", "DesktopPageBrush"),
            ("AniMeidoSurfaceBrush", "DesktopSurfaceBrush"),
            ("AniMeidoCardBackgroundBrush", "DesktopCardBrush"),
            ("AniMeidoCardBorderBrush", "DesktopLineBrush"),
            ("AniMeidoPrimaryTextBrush", "DesktopTextBrush"),
            ("AniMeidoSecondaryTextBrush", "DesktopMutedBrush"),
            ("AniMeidoMutedTextBrush", "DesktopMutedBrush"),
            ("AniMeidoAccentBrush", "DesktopAccentBrush"),
            ("AniMeidoNavIndicatorBrush", "DesktopAccentBrush"),
            ("AniMeidoNavTextDefaultBrush", "DesktopTextBrush"),
            ("AniMeidoNavTextSelectedBrush", "DesktopAccentBrush"),
            ("AniMeidoNavItemSelectedBackgroundBrush", "DesktopSelectedBrush"),
            ("AniMeidoNavItemHoverBackgroundBrush", "DesktopSelectedBrush"),
            ("AniMeidoBadgeBackgroundBrush", "DesktopAccentBrush"),
            ("AniMeidoBadgeTextBrush", "DesktopOnAccentBrush"),
            ("AniMeidoTagBackgroundBrush", "DesktopSelectedBrush"),
            ("AniMeidoTagTextBrush", "DesktopTextBrush"),
            ("AniMeidoStatsCardTextBrush", "DesktopAccentBrush"),
            ("ComboBoxBackgroundBorderBrushFocused", "DesktopFieldFocusBrush"),
            ("ComboBoxBackground", "DesktopFieldBrush"),
            ("ComboBoxBackgroundUnfocused", "DesktopFieldBrush"),
            ("ComboBoxBackgroundFocused", "DesktopFieldFocusBackgroundBrush"),
            ("ComboBoxBackgroundPointerOver", "DesktopFieldHoverBrush"),
            ("ComboBoxBackgroundPressed", "DesktopButtonPressedBrush"),
            ("ComboBoxDropDownBackground", "DesktopOverlayBrush"),
            ("TextControlBackground", "DesktopFieldBrush"),
            ("TextControlBackgroundPointerOver", "DesktopFieldHoverBrush"),
            ("TextControlBackgroundFocused", "DesktopFieldFocusBackgroundBrush"),
            ("FlyoutPresenterBackground", "DesktopOverlayBrush"),
            ("MenuFlyoutPresenterBackground", "DesktopOverlayBrush"),
            ("ComboBoxItemPillFillBrush", "DesktopChoiceIndicatorBrush"),
            ("ExpanderHeaderBackground", "DesktopFieldBrush"),
            ("ExpanderHeaderForeground", "DesktopTextBrush"),
            ("ExpanderHeaderForegroundPointerOver", "DesktopTextBrush"),
            ("ExpanderHeaderForegroundPressed", "DesktopTextBrush"),
            ("ExpanderHeaderBorderBrush", "DesktopLineBrush"),
            ("ExpanderHeaderBorderPointerOverBrush", "DesktopLineBrush"),
            ("ExpanderHeaderBorderPressedBrush", "DesktopLineBrush"),
            ("ExpanderContentBackground", "DesktopSurfaceBrush"),
            ("ExpanderContentBorderBrush", "DesktopLineBrush"),
            ("ListViewItemSelectionIndicatorBrush", "DesktopListIndicatorBrush"),
        })
            _resources[key] = _resources[source];
        foreach (var prefix in new[] { "ListViewItem", "GridViewItem" })
        {
            foreach (var (state, surface) in new[]
            {
                ("", "DesktopTransparentBrush"), ("PointerOver", "DesktopCardHoverBrush"),
                ("Pressed", "DesktopCardPressedBrush"), ("Selected", "DesktopListSelectedBrush"),
                ("SelectedPointerOver", "DesktopSelectionHoverBrush"), ("SelectedPressed", "DesktopSelectionPressedBrush"),
            })
            {
                _resources[$"{prefix}Background{state}"] = _resources[surface];
                _resources[$"{prefix}Foreground{state}"] = _resources[state.StartsWith("Selected", StringComparison.Ordinal)
                    ? "DesktopListSelectedTextBrush" : "DesktopTextBrush"];
            }
            foreach (var state in new[] { "PointerOver", "Selected", "SelectedPointerOver", "SelectedPressed" })
                _resources[$"{prefix}{state}BorderBrush"] = _resources["DesktopTransparentBrush"];
        }
        foreach (var state in new[] { "Selected", "SelectedUnfocused", "SelectedPointerOver", "SelectedPressed" })
        {
            _resources[$"ComboBoxItemBackground{state}"] = _resources["DesktopChoiceSelectedBrush"];
            _resources[$"ComboBoxItemBorderBrush{state}"] = _resources["DesktopChoiceSelectedBrush"];
            _resources[$"ComboBoxItemForeground{state}"] = _resources["DesktopChoiceSelectedTextBrush"];
        }
        foreach (var (state, brush) in new[]
        {
            ("", ""), ("PointerOver", "Hover"), ("Pressed", "Pressed"), ("Disabled", "Disabled"),
        })
        {
            var disabled = state == "Disabled";
            _resources[$"AccentButtonBackground{state}"] = _resources[$"DesktopPrimaryButton{brush}Brush"];
            _resources[$"AccentButtonForeground{state}"] = _resources[disabled ? "DesktopMutedBrush" : "DesktopOnAccentBrush"];
            _resources[$"AccentButtonBorderBrush{state}"] = _resources[disabled ? "DesktopTransparentBrush"
                : state == "Pressed" ? "DesktopPrimaryButtonPressedEdgeBrush"
                : state == "PointerOver" ? "DesktopPrimaryButtonHoverEdgeBrush" : "DesktopPrimaryButtonEdgeBrush"];
            _resources[$"ToggleButtonBackgroundChecked{state}"] = _resources[$"DesktopCheckedButton{brush}Brush"];
            _resources[$"ToggleButtonForegroundChecked{state}"] = _resources[disabled ? "DesktopMutedBrush" : "DesktopCheckedButtonTextBrush"];
            _resources[$"ToggleButtonBorderBrushChecked{state}"] = _resources[disabled ? "DesktopTransparentBrush" : "DesktopCheckedButtonEdgeBrush"];
        }
    }

    public event EventHandler<ElementTheme>? ThemeChanged;
    public event EventHandler<DesktopAppearance>? AppearanceChanged;
    internal event EventHandler? SettingsSaveStateChanged;
    internal bool HasSettingsSaveFailure => _settingsSaveFailed;
    public DesktopAppearance CurrentAppearance => _appearance.Current;

    public async Task InitializeThemeAsync(FrameworkElement rootElement)
    {
        ObjectDisposedException.ThrowIf(_disposeTask is not null, this);
        await _changes.WaitAsync();
        try
        {
            var saved = await _settings.LoadAsync();
            _appearance.Changed -= OnAppearanceChanged;
            _appearance = CreateEditor(new(
                Parse(saved.VisualStyle, DesktopVisualStyle.Fluent11),
                Parse(saved.FontStyle, DesktopFontStyle.System),
                Parse(saved.CatalogCardStyle, DesktopCatalogCardStyle.Cover),
                saved.ShowPlanReminders));
            _root = rootElement;
            _root.ActualThemeChanged += OnActualThemeChanged;
            _systemAnimationsEnabled = _animationSettings.AnimationsEnabled;
            if (_root is Grid grid)
                _toasts = new DesktopToastPresenter(grid);
            _theme = saved.ThemeSetting switch
            {
                "Light" => ElementTheme.Light,
                "Dark" => ElementTheme.Dark,
                _ => ElementTheme.Default,
            };
            _savedTheme = _theme;
            Apply();
        }
        finally
        {
            _changes.Release();
        }
    }

    public Task SetThemeAsync(ElementTheme theme)
    {
        ObjectDisposedException.ThrowIf(_disposeTask is not null, this);
        _theme = theme;
        Apply();
        return ObserveSettingsSaveAsync(SaveThemeAsync(theme));
    }

    private async Task SaveThemeAsync(ElementTheme theme)
    {
        await _changes.WaitAsync();
        try
        {
            await _settings.UpdateAsync(value => value with { ThemeSetting = theme.ToString() });
            _savedTheme = theme;
        }
        finally
        {
            _changes.Release();
        }
    }

    public ElementTheme GetCurrentTheme() => _theme;

    internal void ShowFeedback(string message, bool error = false)
        => _toasts?.Show(message, error, CurrentAppearance.VisualStyle, _resources);

    public Task SetVisualStyleAsync(DesktopVisualStyle style)
        => ObserveSettingsSaveAsync(_appearance.UpdateAsync(value => value with { VisualStyle = style }));

    public Task SetFontStyleAsync(DesktopFontStyle style)
        => ObserveSettingsSaveAsync(_appearance.UpdateAsync(value => value with { FontStyle = style }));

    public Task SetCatalogCardStyleAsync(DesktopCatalogCardStyle style)
        => ObserveSettingsSaveAsync(_appearance.UpdateAsync(value => value with { CatalogCards = style }));

    public Task SetPlanRemindersAsync(bool visible)
        => ObserveSettingsSaveAsync(_appearance.UpdateAsync(value => value with { ShowPlanReminders = visible }));

    internal async Task<bool> RetrySettingsSaveAsync()
    {
        try
        {
            var appearance = _appearance.RetrySaveAsync();
            var theme = SaveThemeAsync(_theme);
            await Task.WhenAll(appearance, theme);
            if (!_appearance.HasUnsavedChanges && _savedTheme == _theme)
                SetSettingsSaveFailure(false);
            return !_settingsSaveFailed;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Serilog.Log.Error(ex, "重试保存桌面设置失败");
            SetSettingsSaveFailure(_appearance.HasUnsavedChanges || _savedTheme != _theme);
            return false;
        }
    }

    private async Task ObserveSettingsSaveAsync(Task save)
    {
        try
        {
            await save;
            if (!_appearance.HasUnsavedChanges && _savedTheme == _theme)
                SetSettingsSaveFailure(false);
        }
        catch
        {
            SetSettingsSaveFailure(_appearance.HasUnsavedChanges || _savedTheme != _theme);
            throw;
        }
    }

    private void SetSettingsSaveFailure(bool failed)
    {
        _settingsSaveFailed = failed;
        if (!failed || CurrentAppearance.VisualStyle != DesktopVisualStyle.Paper)
            _toasts?.Dismiss(SettingsSaveFailureMessage, error: true);
        else if (CurrentAppearance.VisualStyle == DesktopVisualStyle.Paper)
            _toasts?.Show(SettingsSaveFailureMessage, true, CurrentAppearance.VisualStyle, _resources, RetrySettingsSaveAsync);
        SettingsSaveStateChanged?.Invoke(this, EventArgs.Empty);
    }

    public ValueTask DisposeAsync() => new(_disposeTask ??= DetachAsync());

    private DesktopAppearanceEditor CreateEditor(DesktopAppearance initial)
    {
        var editor = new DesktopAppearanceEditor(initial, async (value, token) =>
        {
            await _settings.UpdateAsync(settings => settings with
            {
                VisualStyle = value.VisualStyle.ToString(),
                FontStyle = value.FontStyle.ToString(),
                CatalogCardStyle = value.CatalogCards.ToString(),
                ShowPlanReminders = value.ShowPlanReminders,
            }, token);
        });
        editor.Changed += OnAppearanceChanged;
        return editor;
    }

    private async Task DetachAsync()
    {
        await _appearance.StopAsync();
        await _changes.WaitAsync();
        try
        {
            if (_root is not null)
                _root.ActualThemeChanged -= OnActualThemeChanged;
            DesktopFeedback.Requested -= ShowDesktopFeedback;
            _toasts?.Dispose();
            _toasts = null;
            _root = null;
            _appearance.Changed -= OnAppearanceChanged;
            ThemeChanged = null;
            AppearanceChanged = null;
            SettingsSaveStateChanged = null;
        }
        finally
        {
            _changes.Release();
        }
    }

    private void OnActualThemeChanged(FrameworkElement sender, object args)
    {
        if (!_applying && _appliedDark != (sender.ActualTheme == ElementTheme.Dark))
            Apply();
    }

    private void OnAppearanceChanged(object? sender, DesktopAppearance appearance)
    {
        Apply();
        SetSettingsSaveFailure(_settingsSaveFailed);
        AppearanceChanged?.Invoke(this, appearance);
    }

    internal void RefreshSystemMotionPreference()
    {
        if (_root is null || _disposeTask is not null)
            return;
        // 从系统设置返回时重新读取；仅变化时通知活动动画，避免切窗时重复遍历整页。
        var enabled = _animationSettings.AnimationsEnabled;
        if (enabled == _systemAnimationsEnabled)
            return;
        _systemAnimationsEnabled = enabled;
        _resources["DesktopMotionEnabled"] = enabled;
        DesktopPresentationPreferences.NotifyAppearanceChanged();
        ThemeChanged?.Invoke(this, _theme);
    }

    private void Apply()
    {
        if (_root is null || _applying)
            return;
        _applying = true;
        try
        {
            _root.RequestedTheme = _theme;
            var dark = _root.ActualTheme == ElementTheme.Dark;
            _appliedDark = dark;
            var appearance = CurrentAppearance;
            _resources["DesktopMotionEnabled"] = _systemAnimationsEnabled;
            DesktopFontProvider.Shared.UseSerif = appearance.FontStyle == DesktopFontStyle.Serif;
            var classic = appearance.VisualStyle == DesktopVisualStyle.Fluent10;
            var paper = appearance.VisualStyle == DesktopVisualStyle.Paper;
            var modern = appearance.VisualStyle == DesktopVisualStyle.Fluent11;
            var colors = DesktopAppearancePalette.For(appearance.VisualStyle, dark);
            SetBrush("DesktopPageBrush", colors.Page);
            SetBrush("DesktopNavigationBrush", colors.Navigation);
            SetBrush("DesktopSurfaceBrush", colors.Surface);
            SetBrush("DesktopCardBrush", colors.Card);
            SetBrush("DesktopCardHoverBrush", paper ? dark ? "#383835" : "#F2F0E8"
                : dark ? "#3B3B3B" : "#EEEEEE");
            SetBrush("DesktopCardPressedBrush", paper ? dark ? "#3C3C38" : "#EAE8E1"
                : dark ? "#404040" : "#E8E8E8");
            SetBrush("DesktopSelectionHoverBrush", classic ? dark ? "#1084E0" : "#006CBE"
                : paper ? dark ? "#464642" : "#DDDDD8" : dark ? "#474747" : "#DEDEDE");
            SetBrush("DesktopSelectionPressedBrush", classic ? "#005A9E"
                : paper ? dark ? "#3C3C38" : "#D5D5D0" : dark ? "#3D3D3D" : "#D8D8D8");
            SetBrush("DesktopListSelectedBrush", classic ? colors.Accent : colors.Selected);
            SetBrush("DesktopListSelectedTextBrush", classic ? "#FFFFFF" : colors.Text);
            SetBrush("DesktopListIndicatorBrush", colors.Accent);
            ((Brush)_resources["DesktopListIndicatorBrush"]).Opacity = paper ? 0 : 1;
            SetBrush("DesktopFieldBrush", colors.Field);
            SetBrush("DesktopPlaceholderBrush", colors.Field);
            SetBrush("DesktopFailedOverlayBrush", colors.Surface);
            ((Brush)_resources["DesktopFailedOverlayBrush"]).Opacity = 0.92;
            SetBrush("DesktopFollowingBrush", "#F6B925");
            SetBrush("DesktopFieldHoverBrush", colors.Card);
            SetBrush("DesktopFieldFocusBackgroundBrush", colors.Overlay);
            SetBrush("DesktopOverlayBrush", colors.Overlay);
            SetBrush("DesktopLineBrush", colors.Line);
            SetBrush("DesktopTextBrush", colors.Text);
            SetBrush("DesktopMutedBrush", colors.Muted);
            SetBrush("DesktopDialogBodyBrush", colors.Overlay);
            SetBrush("DesktopDialogFooterBrush", modern ? colors.Page : colors.Overlay);
            SetBrush("DesktopDialogEdgeBrush", classic ? colors.Accent : colors.Line);
            _resources["DesktopDialogRadius"] = _resources[classic ? "DesktopClassicDialogRadius"
                : paper ? "DesktopPaperDialogRadius" : "DesktopModernDialogRadius"];
            _resources["DesktopDialogBorderThickness"] = _resources[classic
                ? "DesktopClassicDialogBorderThickness" : "DesktopStandardDialogBorderThickness"];
            _resources["ContentDialogBorderWidth"] = _resources["DesktopDialogBorderThickness"];
            _resources["DesktopDialogSeparatorThickness"] = new Thickness(0, 0, 0, modern ? 1 : 0);
            _resources["ContentDialogSeparatorThickness"] = _resources["DesktopDialogSeparatorThickness"];
            SetBrush("DesktopAccentBrush", colors.Accent);
            SetBrush("DesktopNavigationIndicatorBrush", colors.Accent);
            ((Brush)_resources["DesktopNavigationIndicatorBrush"]).Opacity = paper ? 0 : 1;
            SetBrush("DesktopSelectedBrush", colors.Selected);
            SetBrush("DesktopNavigationSelectedPointerOverBrush", paper
                ? dark ? "#464642" : "#DDDDD8" : dark ? "#474747" : "#DEDEDE");
            SetBrush("DesktopNavigationSelectedPressedBrush", paper
                ? dark ? "#3C3C38" : "#D5D5D0" : dark ? "#3D3D3D" : "#D0D0D0");
            SetBrush("DesktopSectionSelectedBrush", paper ? colors.Selected : dark ? "#283E53" : "#E7F0FB");
            SetBrush("DesktopCalendarSelectedBrush", paper ? colors.Selected : colors.Accent);
            ((SolidColorBrush)_resources["DesktopCalendarSelectedBrush"]).Opacity = paper ? 1 : 0.14;
            SetBrush("DesktopCalendarSelectedEdgeBrush", paper ? dark ? "#514C42" : "#D4CFC4" : colors.Accent);
            SetBrush("DesktopSettingsSelectedBrush", colors.Selected);
            ((SolidColorBrush)_resources["DesktopSettingsSelectedBrush"]).Opacity = paper ? 1 : 0;
            SetBrush("DesktopSettingsIndicatorBrush", colors.Accent);
            ((SolidColorBrush)_resources["DesktopSettingsIndicatorBrush"]).Opacity = paper ? 0 : 1;
            SetBrush("DesktopSettingRowBrush", colors.Surface);
            ((SolidColorBrush)_resources["DesktopSettingRowBrush"]).Opacity = modern ? 1 : 0;
            SetBrush("DesktopButtonBrush", classic ? dark ? "#333333" : "#CCCCCC" : colors.Field);
            SetBrush("DesktopButtonHoverBrush", classic ? dark ? "#404040" : "#CCCCCC"
                : modern ? dark ? "#333333" : "#F9F9F9" : dark ? "#363636" : "#F5F4EF");
            SetBrush("DesktopButtonPressedBrush", classic ? dark ? "#666666" : "#999999"
                : modern ? dark ? "#292929" : "#F0F0F0" : dark ? "#303030" : "#EAE8E1");
            var hover = ((SolidColorBrush)_resources["DesktopButtonHoverBrush"]).Color;
            // 透明白在 RGB/Alpha 分别插值时会闪白；只让同色底的 Alpha 渐变。
            ((SolidColorBrush)_resources["DesktopSubtleButtonBackgroundBrush"]).Color = Color.FromArgb(0, hover.R, hover.G, hover.B);
            ((SolidColorBrush)_resources["DesktopTransparentBrush"]).Color = Color.FromArgb(0, hover.R, hover.G, hover.B);
            var buttonColors = DesktopAppearanceButtonColors.For(appearance.VisualStyle, dark);
            SetEdgeGradient("DesktopButtonEdgeBrush", buttonColors.ControlEdge, buttonColors.ControlBottomEdge, classic ? 0 : 1);
            var hoverEdge = classic ? dark ? "#858585" : "#7A7A7A"
                : modern ? dark ? "#4F4F4F" : "#DCDCDC" : colors.Line;
            SetEdgeGradient("DesktopButtonHoverEdgeBrush", hoverEdge, modern ? buttonColors.ControlBottomEdge : hoverEdge);
            SetBrush("DesktopButtonPressedEdgeBrush", colors.Line);
            var (primary, primaryHover, primaryPressed) = (buttonColors.Primary, buttonColors.Hover, buttonColors.Pressed);
            SetBrush("DesktopPrimaryButtonBrush", primary);
            SetBrush("DesktopPrimaryButtonHoverBrush", primaryHover);
            SetBrush("DesktopPrimaryButtonPressedBrush", primaryPressed);
            SetBrush("DesktopPrimaryButtonDisabledBrush", primary);
            SetEdgeGradient("DesktopPrimaryButtonEdgeBrush", buttonColors.PrimaryEdge, buttonColors.PrimaryBottomEdge, modern ? 1 : 0);
            SetEdgeGradient("DesktopPrimaryButtonHoverEdgeBrush", modern && dark ? "#78D5FF" : buttonColors.PrimaryEdge,
                buttonColors.PrimaryBottomEdge, modern ? 1 : 0);
            SetBrush("DesktopPrimaryButtonPressedEdgeBrush", buttonColors.PrimaryEdge);
            ((Brush)_resources["DesktopPrimaryButtonPressedEdgeBrush"]).Opacity = modern ? 1 : 0;
            SetBrush("DesktopCheckedButtonBrush", paper ? colors.Selected : primary);
            SetBrush("DesktopCheckedButtonHoverBrush", paper ? dark ? "#454545" : "#DEDEDE" : primaryHover);
            SetBrush("DesktopCheckedButtonPressedBrush", paper ? dark ? "#4D4D4D" : "#D2D2D2" : primaryPressed);
            SetBrush("DesktopCheckedButtonDisabledBrush", paper ? colors.Selected : primary);
            SetBrush("DesktopCheckedButtonTextBrush", paper ? colors.Text : dark && modern ? "#001F33" : "#FFFFFF");
            SetBrush("DesktopCheckedButtonEdgeBrush", paper ? dark ? "#515151" : "#D0D0D0" : primary);
            SetBrush("DesktopChoiceSelectedBrush", classic ? colors.Accent : colors.Selected);
            SetBrush("DesktopChoiceSelectedTextBrush", classic ? "#FFFFFF" : colors.Text);
            SetBrush("DesktopChoiceIndicatorBrush", paper ? "#237CE6" : colors.Accent);
            ((SolidColorBrush)_resources["DesktopChoiceIndicatorBrush"]).Opacity = classic ? 0 : 1;
            SetBrush("DesktopFieldFocusBrush", classic ? colors.Accent : colors.Text);
            SetBrush("DesktopClassicFieldEdgeBrush", dark ? "#858585" : "#8A8A8A");
            SetBrush("DesktopClassicFieldHoverBrush", dark ? "#B8B8B8" : "#555555");
            SetBrush("DesktopTextFieldEdgeBrush", classic ? dark ? "#858585" : "#8A8A8A" : colors.Line);
            SetBrush("DesktopTextFieldHoverEdgeBrush", classic ? dark ? "#B8B8B8" : "#555555" : colors.Muted);
            SetBrush("DesktopTextFieldFocusBrush", paper ? colors.Text : colors.Accent);
            _resources["DesktopTextFieldFocusThickness"] = classic ? new Thickness(2)
                : paper ? new Thickness(1) : new Thickness(1, 1, 1, 2);
            _resources["DesktopUseSegmentedChoices"] = paper;
            _resources["DesktopFocusMargin"] = _resources[classic ? "DesktopClassicFocusMargin"
                : paper ? "DesktopPaperFocusMargin" : "DesktopModernFocusMargin"];
            SetBrush("DesktopFocusVisualBrush", paper ? colors.Accent : colors.Text);
            _resources["DesktopButtonBorderThickness"] = new Thickness(classic ? 2 : 1);
            _resources["DesktopFieldBorderThickness"] = new Thickness(classic ? 2 : 1);
            _resources["DesktopSettingRowBorderThickness"] = modern ? new Thickness(1) : new Thickness(0, 0, 0, 1);
            _resources["DesktopSettingRowPadding"] = modern ? new Thickness(20, 16, 20, 16) : new Thickness(0, 16, 0, 16);
            _resources["DesktopSettingRowMargin"] = modern ? new Thickness(0, 0, 0, 8) : new Thickness(0);
            _resources["ExpanderHeaderBorderThickness"] = new Thickness(modern ? 1 : 0);
            _resources["DesktopSymbolFontFamily"] = new FontFamily(classic
                ? "Segoe MDL2 Assets" : "Segoe Fluent Icons, Segoe MDL2 Assets");
            _resources["SymbolThemeFontFamily"] = _resources["DesktopSymbolFontFamily"];
            SetBrush("DesktopBroadcastBrush", (appearance.VisualStyle, dark) switch
            {
                (DesktopVisualStyle.Fluent10, false) => "#EDF5FC",
                (DesktopVisualStyle.Fluent10, true) => "#102130",
                (DesktopVisualStyle.Paper, false) => "#F2F0E8",
                (DesktopVisualStyle.Paper, true) => "#262624",
                (_, true) => "#27384A",
                _ => "#EAF2F9",
            });
            ApplySegmentPalette(appearance.VisualStyle, dark, colors.Text);
            SetBrush("DesktopOnAccentBrush", buttonColors.OnPrimary);
            var radius = appearance.VisualStyle switch { DesktopVisualStyle.Fluent10 => 0, DesktopVisualStyle.Paper => 6, _ => 4 };
            _resources["DesktopControlRadius"] = new CornerRadius(radius);
            _resources["GridViewItemCornerRadius"] = new CornerRadius(radius);
            _resources["ControlCornerRadius"] = _resources["DesktopControlRadius"];
            _resources["OverlayCornerRadius"] = new CornerRadius(classic ? 0 : paper ? 12 : 8);
            _resources["DesktopCoverRadius"] = new CornerRadius(appearance.VisualStyle switch
            {
                DesktopVisualStyle.Fluent10 => 0, DesktopVisualStyle.Paper => 3, _ => 6,
            });
            _resources["DesktopSectionRadius"] = new CornerRadius(appearance.VisualStyle == DesktopVisualStyle.Fluent11 ? 6 : 0);
            _resources["DesktopCardBorderThickness"] = new Thickness(appearance.VisualStyle == DesktopVisualStyle.Fluent11 ? 1 : 0);
            var font = DesktopAppearanceFont.ForStyle(appearance.FontStyle);
            _resources["DesktopTitleSize"] = font.TitleSize;
            _resources["DesktopSectionSize"] = font.SectionSize;
            _resources["DesktopTitleWeight"] = new Windows.UI.Text.FontWeight { Weight = font.TitleWeight };
            _resources["DesktopSectionWeight"] = new Windows.UI.Text.FontWeight { Weight = font.SectionWeight };
            _resources["DesktopEmphasisWeight"] = appearance.FontStyle == DesktopFontStyle.Serif
                ? FontWeights.Normal : FontWeights.SemiBold;
            _resources["DesktopNavigationRadius"] = new CornerRadius(appearance.VisualStyle == DesktopVisualStyle.Fluent10 ? 0 : 5);
            _resources["DesktopToggleSwitchStyle"] = new Style(typeof(ToggleSwitch))
            {
                BasedOn = appearance.VisualStyle == DesktopVisualStyle.Paper
                    ? (Style)_resources["DesktopPaperToggleSwitchStyle"]
                    : _nativeToggleSwitchStyle,
            };
            SetBrush("DesktopSwitchOffBrush", dark ? "#343434" : "#D0D0D0");
            SetBrush("DesktopSwitchOffHoverBrush", dark ? "#484848" : "#AAAAAA");
            _resources["DesktopUiFontFamily"] = new FontFamily(font.Family);
            _resources["DesktopReadingFontFamily"] = new FontFamily(font.ReadingFamily);
            _resources["ContentControlThemeFontFamily"] = _resources["DesktopUiFontFamily"];
            _resources["DesktopBodySize"] = font.BodySize;
            _resources["DesktopBodyLineHeight"] = font.BodySize * font.BodyLineHeight;
            _resources["DesktopControlTextSize"] = font.ControlSize;
            _resources["DesktopDataTextSize"] = font.DataSize;
            DesktopPresentationPreferences.Apply(
                appearance.CatalogCards == DesktopCatalogCardStyle.Detailed,
                appearance.ShowPlanReminders);
            DesktopFeedback.Requested -= ShowDesktopFeedback;
            if (appearance.VisualStyle == DesktopVisualStyle.Paper)
                DesktopFeedback.Requested += ShowDesktopFeedback;
            DesktopPresentationPreferences.NotifyAppearanceChanged();
            ThemeChanged?.Invoke(this, _theme);
        }
        finally
        {
            _applying = false;
        }
    }

    private void SetBrush(string key, string hex)
    {
        var color = ParseColor(hex);
        if (_resources.TryGetValue(key, out var value) && value is SolidColorBrush brush)
            brush.Color = color;
        else
            _resources[key] = new SolidColorBrush(color);
    }

    private void SetEdgeGradient(string key, string top, string bottom, double opacity = 1)
    {
        var brush = (LinearGradientBrush)_resources[key];
        brush.GradientStops[0].Color = ParseColor(bottom);
        brush.GradientStops[1].Color = ParseColor(top);
        brush.Opacity = opacity;
    }

    private void ShowDesktopFeedback(string message, bool error) => ShowFeedback(message, error);

    private void ApplySegmentPalette(DesktopVisualStyle visual, bool dark, string text)
    {
        var classic = visual == DesktopVisualStyle.Fluent10;
        var paper = visual == DesktopVisualStyle.Paper;
        _resources["DesktopSegmentTrackRadius"] = new CornerRadius(classic ? 0 : paper ? 9 : 7);
        _resources["DesktopSegmentItemRadius"] = new CornerRadius(classic ? 0 : paper ? 7 : 4);
        SetBrush("DesktopSegmentTrackBrush", dark ? "#2B2B2B" : paper ? "#F0F0F0" : "#ECECEC");
        SetBrush("DesktopSegmentHoverBrush", dark ? "#363636" : "#E2E2E2");
        SetBrush("DesktopSegmentPressedBrush", dark ? "#454545" : "#D9D9D9");
        SetBrush("DesktopSegmentSelectedBrush", classic ? "#0078D7" : dark ? "#414141" : "#FFFFFF");
        SetBrush("DesktopSegmentSelectedEdgeBrush", classic ? "#0078D7" : dark ? "#515151" : "#DEDEDE");
        SetBrush("DesktopSegmentSelectedTextBrush", classic ? "#FFFFFF" : text);
        SetBrush("DesktopSegmentFocusBrush", text);
    }


    private static T Parse<T>(string value, T fallback) where T : struct, Enum
        => Enum.TryParse<T>(value, out var parsed) && Enum.IsDefined(parsed) ? parsed : fallback;

    private static Color ParseColor(string hex) => Color.FromArgb(255,
        Convert.ToByte(hex.Substring(1, 2), 16), Convert.ToByte(hex.Substring(3, 2), 16),
        Convert.ToByte(hex.Substring(5, 2), 16));

}
