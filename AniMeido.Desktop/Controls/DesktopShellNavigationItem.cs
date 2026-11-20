using AniMeido.App.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;

namespace AniMeido.App.Controls;

public sealed class DesktopShellNavigationItem : ToggleButton
{
    private readonly TextBlock _caption;
    private readonly FontIcon _icon;
    private readonly Grid _content;
    private Border? _indicator;
    private DesktopVisualStyle _visual;

    public DesktopShellNavigationItem(string label, string glyph)
    {
        _icon = new FontIcon { Glyph = glyph, FontSize = 18, VerticalAlignment = VerticalAlignment.Center };
        _caption = new TextBlock { Text = label, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        _content = new Grid { ColumnSpacing = 12 };
        _content.ColumnDefinitions.Add(new ColumnDefinition { Width = new(20) });
        _content.ColumnDefinitions.Add(new ColumnDefinition { Width = new(1, GridUnitType.Star) });
        Grid.SetColumn(_caption, 1);
        _content.Children.Add(_icon);
        _content.Children.Add(_caption);
        Content = _content;
        AutomationProperties.SetName(this, label);
    }

    internal string Caption => _caption.Text;
    internal bool CaptionVisible => _caption.Visibility == Visibility.Visible;
    internal double SelectionIndicatorWidth => _indicator?.Width ?? 0;
    internal bool CaptionFits => _caption.ActualHeight + Padding.Top + Padding.Bottom <= ActualHeight + 0.5;

    internal void ApplyAppearance(DesktopVisualStyle visual, bool expanded, ResourceDictionary resources)
    {
        _visual = visual;
        var classic = visual == DesktopVisualStyle.Fluent10;
        var paper = visual == DesktopVisualStyle.Paper;
        MinHeight = classic || paper ? 44 : 36;
        CornerRadius = new(classic ? 0 : paper ? 6 : 4);
        var inset = classic ? 0 : !expanded ? 4 : paper ? 12 : 8;
        Margin = new(inset, classic ? 0 : 2, inset, classic ? 0 : 2);
        Padding = expanded ? new Thickness(classic ? 16 : 12, 8, 12, 8) : new Thickness(8);
        _caption.FontFamily = FontFamily = (FontFamily)resources["DesktopUiFontFamily"];
        _caption.FontSize = FontSize = (double)resources["DesktopControlTextSize"];
        _icon.FontFamily = (FontFamily)resources["DesktopSymbolFontFamily"];
        _caption.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        _content.ColumnDefinitions[1].Width = expanded ? new(1, GridUnitType.Star) : new(0);
        _content.ColumnSpacing = expanded ? 12 : 0;
        _content.HorizontalAlignment = expanded ? HorizontalAlignment.Stretch : HorizontalAlignment.Center;
        ApplyParts();
    }

    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        _indicator = GetTemplateChild("SelectionIndicator") as Border;
        ApplyParts();
    }

    private void ApplyParts()
    {
        if (_indicator is null) return;
        _indicator.Width = _visual == DesktopVisualStyle.Paper ? 0 : _visual == DesktopVisualStyle.Fluent10 ? 4 : 3;
        _indicator.Height = _visual == DesktopVisualStyle.Fluent10 ? 24 : 16;
        _indicator.CornerRadius = new(_visual == DesktopVisualStyle.Fluent10 ? 0 : 2);
    }
}
