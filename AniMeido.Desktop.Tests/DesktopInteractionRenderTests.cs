using AniMeido.App.Services;
using AniMeido.Contracts;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.UI;
using Xunit;

namespace AniMeido.Desktop.Tests;

[Collection(UnoHeadlessTestHost.CollectionName)]
public sealed class DesktopInteractionRenderTests
{
    public static TheoryData<DesktopVisualStyle, bool> Modes => new()
    {
        { DesktopVisualStyle.Fluent10, false }, { DesktopVisualStyle.Fluent10, true },
        { DesktopVisualStyle.Fluent11, false }, { DesktopVisualStyle.Fluent11, true },
        { DesktopVisualStyle.Paper, false }, { DesktopVisualStyle.Paper, true },
    };

    [Theory]
    [MemberData(nameof(Modes))]
    public Task ButtonsReturnToTheirOwnNormalSurfaceAfterHoverPressAndDisable(DesktopVisualStyle visual, bool dark)
        => WithThemeAsync(visual, dark, async root =>
        {
            var resources = Application.Current.Resources;
            foreach (var (style, hover, pressed) in new[]
            {
                ("DesktopButtonStyle", "DesktopButtonHoverBrush", "DesktopButtonPressedBrush"),
                ("DesktopAccentButtonStyle", "DesktopPrimaryButtonHoverBrush", "DesktopPrimaryButtonPressedBrush"),
                ("DesktopSubtleButtonStyle", "DesktopButtonHoverBrush", "DesktopButtonPressedBrush"),
                ("DesktopCardButtonStyle", "DesktopCardHoverBrush", "DesktopCardPressedBrush"),
            })
            {
                var button = CreateButton(style);
                root.Children.Add(button);
                root.UpdateLayout();
                await Task.Delay(20);
                var surface = FindSurface(button);
                Assert.Null(surface.BackgroundTransition); // 真实指针进入后才启用，详见首帧回归。
                var normal = ColorOf(surface.Background);
                Assert.True(VisualStateManager.GoToState(button, "PointerOver", false));
                Assert.Equal(ColorOf((Brush)resources[hover]), ColorOf(surface.Background));
                Assert.True(VisualStateManager.GoToState(button, "Pressed", false));
                Assert.Equal(ColorOf((Brush)resources[pressed]), ColorOf(surface.Background));
                Assert.True(VisualStateManager.GoToState(button, "Normal", false));
                Assert.Equal(normal, ColorOf(surface.Background));
                button.IsEnabled = false;
                Assert.Equal((double)resources["DesktopDisabledOpacity"], surface.Opacity);
                button.IsEnabled = true;
                Assert.Equal(1, surface.Opacity);
                Assert.Equal(normal, ColorOf(surface.Background));
                Assert.True(button.UseSystemFocusVisuals);
                root.Children.Remove(button);
            }
        });

    [Theory]
    [MemberData(nameof(Modes))]
    public Task ToggleKeepsItsCheckedValueWhilePointerFeedbackChanges(DesktopVisualStyle visual, bool dark)
        => WithThemeAsync(visual, dark, async root =>
        {
            var resources = Application.Current.Resources;
            var toggle = new ToggleButton
            {
                Style = (Style)resources["DesktopToggleButtonStyle"],
                Content = "Selected", Width = 220, Height = 48,
                HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
            };
            root.Children.Add(toggle);
            root.UpdateLayout();
            await Task.Delay(20);
            var surface = FindSurface(toggle);
            toggle.IsChecked = true;
            Assert.Equal(ColorOf((Brush)resources["DesktopCheckedButtonBrush"]), ColorOf(surface.Background));
            foreach (var (state, brush) in new[]
            {
                ("CheckedPointerOver", "DesktopCheckedButtonHoverBrush"),
                ("CheckedPressed", "DesktopCheckedButtonPressedBrush"),
                ("Checked", "DesktopCheckedButtonBrush"),
            })
            {
                Assert.True(VisualStateManager.GoToState(toggle, state, false));
                Assert.True(toggle.IsChecked);
                Assert.Equal(ColorOf((Brush)resources[brush]), ColorOf(surface.Background));
                Assert.Equal(ColorOf((Brush)resources["DesktopCheckedButtonTextBrush"]), ColorOf(surface.Foreground));
            }
            toggle.IsEnabled = false;
            Assert.Equal((double)resources["DesktopDisabledOpacity"], surface.Opacity);
            toggle.IsEnabled = true;
            toggle.IsChecked = false;
            Assert.Equal(ColorOf(toggle.Background), ColorOf(surface.Background));
            Assert.Equal(1, surface.Opacity);
        });

    [Fact]
    public Task PaperSubtleButtonFadesToItsParentWithoutAWhiteFlash()
        => WithThemeAsync(DesktopVisualStyle.Paper, true, async root =>
        {
            var resources = Application.Current.Resources;
            var hover = ColorOf((Brush)resources["DesktopButtonHoverBrush"]);
            var transparent = ColorOf((Brush)resources["DesktopSubtleButtonBackgroundBrush"]);
            Assert.Equal(0, transparent.A);
            Assert.Equal((hover.R, hover.G, hover.B), (transparent.R, transparent.G, transparent.B));
            root.Background = (Brush)resources["DesktopSurfaceBrush"];
            var background = ColorOf(root.Background);
            var button = CreateButton("DesktopSubtleButtonStyle");
            root.Children.Add(button);
            root.UpdateLayout();
            await Task.Delay(20);
            var surface = FindSurface(button);
            surface.BackgroundTransition = null;
            VisualStateManager.GoToState(button, "PointerOver", false);
            root.UpdateLayout();
            await Task.Delay(150);
            Assert.Equal(hover, await PixelAtAsync(root, 12, 12));

            // 拉长过渡，让慢速 CI 也能采到中间帧。
            surface.BackgroundTransition = new BrushTransition { Duration = TimeSpan.FromMilliseconds(750) };
            VisualStateManager.GoToState(button, "Normal", false);
            for (var frame = 0; frame < 4; frame++)
            {
                await Task.Delay(100);
                var pixel = await PixelAtAsync(root, 12, 12);
                Assert.InRange(pixel.R, Math.Min(background.R, hover.R) - 1, Math.Max(background.R, hover.R) + 1);
                Assert.InRange(pixel.G, Math.Min(background.G, hover.G) - 1, Math.Max(background.G, hover.G) + 1);
                Assert.InRange(pixel.B, Math.Min(background.B, hover.B) - 1, Math.Max(background.B, hover.B) + 1);
            }
            await Task.Delay(800);
            Assert.Equal(background, await PixelAtAsync(root, 12, 12));
        });

    [Theory]
    [MemberData(nameof(Modes))]
    public Task TextFieldsKeepEditingAndThemeFocusGeometry(DesktopVisualStyle visual, bool dark)
        => WithThemeAsync(visual, dark, async root =>
        {
            var field = new TextBox
            {
                Text = "abcdef", Width = 240,
                HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
            };
            root.Children.Add(field);
            root.UpdateLayout();
            await Task.Delay(20);
            field.Select(2, 2);
            Assert.Equal("cd", field.SelectedText);
            field.SelectedText = "XY";
            Assert.Equal("abXYef", field.Text);
            Assert.True(VisualStateManager.GoToState(field, "Focused", false));
            var border = FindChild<Border>(field, "BorderElement");
            Assert.Equal((Thickness)Application.Current.Resources["DesktopTextFieldFocusThickness"], border.BorderThickness);
            Assert.Equal((CornerRadius)Application.Current.Resources["DesktopControlRadius"], border.CornerRadius);
            Assert.Equal(ColorOf((Brush)Application.Current.Resources["DesktopTextFieldFocusBrush"]), ColorOf(border.BorderBrush));
        });

    private static Button CreateButton(string style) => new()
    {
        Style = (Style)Application.Current.Resources[style], Content = "Action", Width = 220, Height = 48,
        HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top,
    };

    private static ContentPresenter FindSurface(DependencyObject node)
    {
        if (node is ContentPresenter { Name: "Surface" } surface)
            return surface;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(node); ++index)
        {
            var child = VisualTreeHelper.GetChild(node, index);
            if (child is ContentPresenter { Name: "Surface" } presenter)
                return presenter;
        }
        throw new InvalidOperationException("控件没有自己的 Surface。");
    }

    private static T FindChild<T>(DependencyObject node, string name) where T : FrameworkElement
    {
        if (node is T element && element.Name == name) return element;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(node); index++)
        {
            if (TryFind(VisualTreeHelper.GetChild(node, index)) is { } found) return found;
        }
        throw new InvalidOperationException($"模板缺少 {name}。");

        T? TryFind(DependencyObject parent)
        {
            if (parent is T candidate && candidate.Name == name) return candidate;
            for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
                if (TryFind(VisualTreeHelper.GetChild(parent, index)) is { } found) return found;
            return null;
        }
    }

    private static Color ColorOf(Brush brush) => Assert.IsType<SolidColorBrush>(brush).Color;

    private static async Task<Color> PixelAtAsync(UIElement element, int x, int y)
    {
        var bitmap = new RenderTargetBitmap();
        await bitmap.RenderAsync(element);
        var bytes = (await bitmap.GetPixelsAsync()).ToArray();
        var offset = (y * bitmap.PixelWidth + x) * 4;
        return Color.FromArgb(bytes[offset + 3], bytes[offset + 2], bytes[offset + 1], bytes[offset]);
    }

    internal static async Task WithThemeAsync(DesktopVisualStyle visual, bool dark, Func<Grid, Task> action,
        DesktopFontStyle font = DesktopFontStyle.System)
    {
        var directory = Path.Combine(Path.GetTempPath(), "AniMeido-ui-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            await UnoHeadlessTestHost.RunAsync(async root =>
            {
                var store = new DesktopSettingsStore(new TestPaths(directory));
                await store.SaveAsync(new DesktopSettings { VisualStyle = visual.ToString(), ThemeSetting = dark ? "Dark" : "Light", FontStyle = font.ToString() });
                await using var theme = new ThemeService(store);
                await theme.InitializeThemeAsync(root);
                await action(root);
            });
        }
        finally { Directory.Delete(directory, true); }
    }

    private sealed record TestPaths(string Directory) : IAppDataPaths
    {
        public string DatabasePath => Path.Combine(Directory, "test.db");
        public string BackupDirectory => Path.Combine(Directory, "Backups");
        public string LogDirectory => Path.Combine(Directory, "logs");
    }
}
