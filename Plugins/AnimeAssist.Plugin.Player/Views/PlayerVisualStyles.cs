using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace AniMeido.Plugin.Player.Views;

internal static class PlayerVisualStyles
{
    public static SolidColorBrush WindowBackground { get; } =
        new(ColorHelper.FromArgb(255, 15, 16, 24));

    public static SolidColorBrush SurfaceBackground { get; } =
        new(ColorHelper.FromArgb(255, 27, 29, 42));

    public static SolidColorBrush SurfaceStroke { get; } =
        new(ColorHelper.FromArgb(72, 255, 255, 255));

    public static SolidColorBrush Accent { get; } =
        new(ColorHelper.FromArgb(255, 96, 165, 250));

    public static SolidColorBrush Danger { get; } =
        new(ColorHelper.FromArgb(255, 248, 113, 113));

    public static TextBlock CreatePageTitle(string text)
        => new()
        {
            Text = text,
            FontSize = 28,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        };

    public static TextBlock CreateSubtitle(string text)
        => new()
        {
            Text = text,
            FontSize = 13,
            Opacity = 0.68,
            TextWrapping = TextWrapping.Wrap,
        };

    public static void StyleButton(
        Button button,
        PlayerButtonTone tone = PlayerButtonTone.Secondary)
    {
        button.MinHeight = 36;
        button.MinWidth = 0;
        button.Padding = new Thickness(14, 7, 14, 7);
        button.CornerRadius = new CornerRadius(8);

        if (tone == PlayerButtonTone.Primary)
        {
            button.Background = Accent;
            button.Foreground = new SolidColorBrush(Colors.Black);
        }
        else if (tone == PlayerButtonTone.Danger)
        {
            button.Foreground = Danger;
        }
    }
}

internal enum PlayerButtonTone
{
    Secondary,
    Primary,
    Danger,
}
