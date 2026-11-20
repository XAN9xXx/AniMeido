using Microsoft.UI.Xaml;
using AniMeido.Plugin.Base.Services;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using FontWeight = Windows.UI.Text.FontWeight;

namespace AniMeido.App.Services;

public enum DesktopElementRole
{
    None, Control, SubtleControl, CardControl, Title, Section, Emphasis, Data, EmphasisData, Reading,
    Symbol, SectionSurface, SettingRow, UnderlinedSection, FilledSection, Segments, SegmentItem,
}

public sealed class DesktopElementAppearance : DependencyObject
{
    public static readonly DependencyProperty RoleProperty = DependencyProperty.RegisterAttached(
        "Role", typeof(DesktopElementRole), typeof(DesktopElementAppearance),
        new PropertyMetadata(DesktopElementRole.None, OnRoleChanged));
    public static readonly DependencyProperty PreserveFontSizeProperty = DependencyProperty.RegisterAttached(
        "PreserveFontSize", typeof(bool), typeof(DesktopElementAppearance), new PropertyMetadata(false));
    private static readonly DependencyProperty SubscriptionProperty = DependencyProperty.RegisterAttached(
        "Subscription", typeof(ElementSubscription), typeof(DesktopElementAppearance), new PropertyMetadata(null));

    public static DesktopElementRole GetRole(DependencyObject element) => (DesktopElementRole)element.GetValue(RoleProperty);
    public static void SetRole(DependencyObject element, DesktopElementRole value) => element.SetValue(RoleProperty, value);
    public static bool GetPreserveFontSize(DependencyObject element) => (bool)element.GetValue(PreserveFontSizeProperty);
    public static void SetPreserveFontSize(DependencyObject element, bool value) => element.SetValue(PreserveFontSizeProperty, value);

    private static void OnRoleChanged(DependencyObject element, DependencyPropertyChangedEventArgs args)
    {
        if (element is not FrameworkElement target) return;
        var subscription = (ElementSubscription?)target.GetValue(SubscriptionProperty);
        if (subscription is null)
        {
            subscription = new ElementSubscription(target);
            target.SetValue(SubscriptionProperty, subscription);
        }
        subscription.SetEnabled((DesktopElementRole)args.NewValue != DesktopElementRole.None);
    }

    private sealed class ElementSubscription(FrameworkElement element)
    {
        private bool _enabled;
        private bool _subscribed;
        private bool _captured;
        private bool _localSize;
        private bool _localBorderThickness;
        private FontWeight _originalWeight;

        internal void SetEnabled(bool enabled)
        {
            if (_enabled != enabled)
            {
                _enabled = enabled;
                if (enabled)
                {
                    element.Loaded += OnLoaded;
                    element.Unloaded += OnUnloaded;
                }
                else
                {
                    element.Loaded -= OnLoaded;
                    element.Unloaded -= OnUnloaded;
                    Detach();
                }
            }
            if (enabled && element.IsLoaded) Attach();
        }

        private void OnLoaded(object sender, RoutedEventArgs args) => Attach();
        private void OnUnloaded(object sender, RoutedEventArgs args) => Detach();
        private void Attach()
        {
            if (!_subscribed)
            {
                _subscribed = true;
                DesktopPresentationPreferences.AppearanceChanged += OnAppearanceChanged;
            }
            if (!_captured)
            {
                _captured = true;
                if (element is TextBlock or Control)
                {
                    var property = element is TextBlock ? TextBlock.FontSizeProperty : Control.FontSizeProperty;
                    _localSize = element.ReadLocalValue(property) != DependencyProperty.UnsetValue;
                }
                _originalWeight = element is TextBlock text ? text.FontWeight
                    : element is Control control ? control.FontWeight : FontWeights.Normal;
                _localBorderThickness = element is Control && element.ReadLocalValue(Control.BorderThicknessProperty) != DependencyProperty.UnsetValue;
            }
            Apply();
        }
        private void Detach()
        {
            if (!_subscribed) return;
            _subscribed = false;
            DesktopPresentationPreferences.AppearanceChanged -= OnAppearanceChanged;
        }
        private void OnAppearanceChanged(object? sender, EventArgs args) => Apply();

        private void Apply()
        {
            var resources = Application.Current.Resources;
            var role = GetRole(element);
            var family = (FontFamily)resources[role == DesktopElementRole.Reading
                ? "DesktopReadingFontFamily" : role == DesktopElementRole.Symbol ? "DesktopSymbolFontFamily" : "DesktopUiFontFamily"];
            var serif = ((FontFamily)resources["DesktopUiFontFamily"]).Source.Contains("DesktopSerif", StringComparison.Ordinal);
            var sizeKey = role switch
            {
                DesktopElementRole.Title => "DesktopTitleSize",
                DesktopElementRole.Section => "DesktopSectionSize",
                DesktopElementRole.Data or DesktopElementRole.EmphasisData => "DesktopDataTextSize",
                DesktopElementRole.Reading => "DesktopBodySize",
                _ => "DesktopControlTextSize",
            };
            var weight = role switch
            {
                DesktopElementRole.Title => (FontWeight)resources["DesktopTitleWeight"],
                DesktopElementRole.Section => (FontWeight)resources["DesktopSectionWeight"],
                DesktopElementRole.Emphasis or DesktopElementRole.EmphasisData => (FontWeight)resources["DesktopEmphasisWeight"],
                _ => serif ? FontWeights.Normal : _originalWeight,
            };
            var setSize = !_localSize && !GetPreserveFontSize(element) && role != DesktopElementRole.Symbol;
            if (element is TextBlock text)
            {
                text.FontFamily = family;
                if (role != DesktopElementRole.Symbol) text.FontWeight = weight;
                if (setSize) text.FontSize = (double)resources[sizeKey];
                if (role == DesktopElementRole.Reading) text.LineHeight = (double)resources["DesktopBodyLineHeight"];
            }
            if (element is Control control)
            {
                control.FontFamily = family;
                control.FontWeight = weight;
                if (setSize) control.FontSize = (double)resources[sizeKey];
                control.CornerRadius = (CornerRadius)resources["DesktopControlRadius"];
                if (control is ComboBox or TextBox)
                    control.BorderThickness = (Thickness)resources["DesktopFieldBorderThickness"];
                if (control is ButtonBase button)
                {
                    if (!_localBorderThickness)
                        button.BorderThickness = (Thickness)resources[role switch
                        {
                            DesktopElementRole.SubtleControl or DesktopElementRole.UnderlinedSection or DesktopElementRole.FilledSection
                                => "DesktopSubtleButtonBorderThickness",
                            DesktopElementRole.CardControl => "DesktopCardBorderThickness",
                            _ => "DesktopButtonBorderThickness",
                        }];
                    button.FocusVisualPrimaryBrush = (Brush)resources["DesktopFocusVisualBrush"];
                    button.FocusVisualSecondaryBrush = (Brush)resources["DesktopPageBrush"];
                    button.FocusVisualMargin = (Thickness)resources["DesktopFocusMargin"];
                    button.FocusVisualPrimaryThickness = (Thickness)resources["DesktopFocusThickness"];
                }
                if (control is ToggleSwitch toggle)
                    toggle.Style = (Style)resources["DesktopToggleSwitchStyle"];
                if (control is ComboBox combo)
                    combo.ItemContainerStyle = (Style)resources[(bool)resources["DesktopUseSegmentedChoices"]
                        ? "DesktopPaperComboBoxItemStyle" : "DesktopNativeComboBoxItemStyle"];
                if (control is ContentDialog dialog)
                {
                    dialog.CornerRadius = (CornerRadius)resources["DesktopDialogRadius"];
                    dialog.BorderThickness = (Thickness)resources["DesktopDialogBorderThickness"];
                    dialog.BorderBrush = (Brush)resources["DesktopDialogEdgeBrush"];
                    dialog.Background = (Brush)resources["DesktopDialogFooterBrush"];
                }
                if (role is DesktopElementRole.UnderlinedSection or DesktopElementRole.FilledSection)
                    control.Template = (ControlTemplate)resources[(bool)resources["DesktopUseSegmentedChoices"]
                        ? "DesktopToggleButtonTemplate" : role == DesktopElementRole.UnderlinedSection
                            ? "DesktopWindowsSectionTabTemplate" : "DesktopFilledSectionTabTemplate"];
                if (role is DesktopElementRole.Segments or DesktopElementRole.SegmentItem)
                    control.CornerRadius = (CornerRadius)resources[role == DesktopElementRole.Segments
                        ? "DesktopSegmentTrackRadius" : "DesktopSegmentItemRadius"];
            }
            if (element is Border border && role is (DesktopElementRole.SectionSurface or DesktopElementRole.SettingRow))
            {
                border.CornerRadius = (CornerRadius)resources["DesktopSectionRadius"];
                border.BorderThickness = (Thickness)resources[role == DesktopElementRole.SettingRow
                    ? "DesktopSettingRowBorderThickness" : "DesktopCardBorderThickness"];
                if (role == DesktopElementRole.SettingRow)
                {
                    border.Padding = (Thickness)resources["DesktopSettingRowPadding"];
                    border.Margin = (Thickness)resources["DesktopSettingRowMargin"];
                }
            }
            if (element is RichTextBlock rich) rich.FontFamily = family;
        }
    }
}
