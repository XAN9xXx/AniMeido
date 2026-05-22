using Microsoft.UI.Composition;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using System.Collections.ObjectModel;
using AniMeido.App.Services;
using Microsoft.Extensions.DependencyInjection;

namespace AniMeido.App.Views
{
    public sealed partial class SettingPage : Page
    {
        public SettingPage()
        {
            InitializeComponent();

            // 初始化主题选中索引
            var current = App.ThemeService.GetCurrentTheme();
            ThemeCombo.SelectedIndex = current switch
            {
                ElementTheme.Light => 0,
                ElementTheme.Dark => 1,
                _ => 2,
            };

            // 填充插件列表
            if (App.Plugins is not null)
            {
                foreach (var plugin in App.Plugins)
                    PluginList.Items.Add(plugin);
            }

            // GitHub 按钮缩放动画中心点
            GitHubButton.SizeChanged += (s, e) =>
            {
                var visual = ElementCompositionPreview.GetElementVisual(GitHubButton);
                visual.CenterPoint = new System.Numerics.Vector3(
                    (float)e.NewSize.Width / 2,
                    (float)e.NewSize.Height / 2,
                    0);
            };

            // 官网按钮缩放动画中心点
            WebsiteButton.SizeChanged += (s, e) =>
            {
                var visual = ElementCompositionPreview.GetElementVisual(WebsiteButton);
                visual.CenterPoint = new System.Numerics.Vector3(
                    (float)e.NewSize.Width / 2,
                    (float)e.NewSize.Height / 2,
                    0);
            };
        }

        private void OnThemeSelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (ThemeCombo.SelectedIndex < 0) return;

            var theme = ThemeCombo.SelectedIndex switch
            {
                0 => ElementTheme.Light,
                1 => ElementTheme.Dark,
                _ => ElementTheme.Default,
            };

            App.ThemeService.SetTheme(theme);
        }

        private async void OnGitHubCardTapped(object sender, TappedRoutedEventArgs e)
        {
            await Windows.System.Launcher.LaunchUriAsync(new Uri("https://github.com/XAN9xXx/AniMeido"));
        }

        private void OnGitHubPointerEntered(object sender, PointerRoutedEventArgs e)
        {
            var visual = ElementCompositionPreview.GetElementVisual(GitHubButton);
            var compositor = visual.Compositor;

            visual.Properties.InsertVector3("Translation", new System.Numerics.Vector3(0, 0, 16));

            var scaleX = compositor.CreateScalarKeyFrameAnimation();
            scaleX.InsertKeyFrame(1.0f, 1.05f);
            scaleX.Duration = TimeSpan.FromMilliseconds(200);

            var scaleY = compositor.CreateScalarKeyFrameAnimation();
            scaleY.InsertKeyFrame(1.0f, 1.05f);
            scaleY.Duration = TimeSpan.FromMilliseconds(200);

            visual.StartAnimation("Scale.X", scaleX);
            visual.StartAnimation("Scale.Y", scaleY);
        }

        private void OnGitHubPointerExited(object sender, PointerRoutedEventArgs e)
        {
            var visual = ElementCompositionPreview.GetElementVisual(GitHubButton);
            var compositor = visual.Compositor;

            visual.Properties.InsertVector3("Translation", new System.Numerics.Vector3(0, 0, 0));

            var scaleX = compositor.CreateScalarKeyFrameAnimation();
            scaleX.InsertKeyFrame(1.0f, 1.0f);
            scaleX.Duration = TimeSpan.FromMilliseconds(200);

            var scaleY = compositor.CreateScalarKeyFrameAnimation();
            scaleY.InsertKeyFrame(1.0f, 1.0f);
            scaleY.Duration = TimeSpan.FromMilliseconds(200);

            visual.StartAnimation("Scale.X", scaleX);
            visual.StartAnimation("Scale.Y", scaleY);
        }

        private void OnGitHubPointerPressed(object sender, PointerRoutedEventArgs e)
        {
            var visual = ElementCompositionPreview.GetElementVisual(GitHubButton);
            var compositor = visual.Compositor;

            var scaleX = compositor.CreateScalarKeyFrameAnimation();
            scaleX.InsertKeyFrame(1.0f, 0.95f);
            scaleX.Duration = TimeSpan.FromMilliseconds(100);

            var scaleY = compositor.CreateScalarKeyFrameAnimation();
            scaleY.InsertKeyFrame(1.0f, 0.95f);
            scaleY.Duration = TimeSpan.FromMilliseconds(100);

            visual.StartAnimation("Scale.X", scaleX);
            visual.StartAnimation("Scale.Y", scaleY);
        }

        private void OnGitHubPointerReleased(object sender, PointerRoutedEventArgs e)
        {
            var visual = ElementCompositionPreview.GetElementVisual(GitHubButton);
            var compositor = visual.Compositor;

            var scaleX = compositor.CreateScalarKeyFrameAnimation();
            scaleX.InsertKeyFrame(1.0f, 1.05f);
            scaleX.Duration = TimeSpan.FromMilliseconds(100);

            var scaleY = compositor.CreateScalarKeyFrameAnimation();
            scaleY.InsertKeyFrame(1.0f, 1.05f);
            scaleY.Duration = TimeSpan.FromMilliseconds(100);

            visual.StartAnimation("Scale.X", scaleX);
            visual.StartAnimation("Scale.Y", scaleY);
        }

        private async void OnWebsiteCardTapped(object sender, TappedRoutedEventArgs e)
        {
            await Windows.System.Launcher.LaunchUriAsync(new Uri("https://animeido.com"));
        }

        private void OnWebsitePointerEntered(object sender, PointerRoutedEventArgs e) => AnimateButton(WebsiteButton, 1.05f, 16);
        private void OnWebsitePointerExited(object sender, PointerRoutedEventArgs e) => AnimateButton(WebsiteButton, 1.0f, 0);
        private void OnWebsitePointerPressed(object sender, PointerRoutedEventArgs e) => AnimateButton(WebsiteButton, 0.95f, 16);
        private void OnWebsitePointerReleased(object sender, PointerRoutedEventArgs e) => AnimateButton(WebsiteButton, 1.05f, 16);

        private async void OnUpdateCheckClick(object sender, RoutedEventArgs e)
        {
            UpdateCheckButton.IsEnabled = false;
            UpdateCheckButton.Content = "检查中…";

            try
            {
                var updateService = App.Services?.GetRequiredService<UpdateService>();
                if (updateService == null) return;

                var result = await updateService.CheckForUpdateAsync();

                ContentDialog dialog;
                if (result == null)
                {
                    dialog = new ContentDialog
                    {
                        Title = "检查更新",
                        Content = "检查更新失败，请稍后重试或检查网络连接。",
                        CloseButtonText = "确定",
                        XamlRoot = this.XamlRoot
                    };
                }
                else if (result.HasUpdate)
                {
                    dialog = new ContentDialog
                    {
                        Title = "发现新版本",
                        Content = $"最新版本：{result.LatestVersion}\n\n{result.ReleaseNotes}\n\n如果下载缓慢，请尝试使用 Motrix 等工具加速下载。",
                        PrimaryButtonText = "下载更新",
                        CloseButtonText = "稍后再说",
                        DefaultButton = ContentDialogButton.Primary,
                        XamlRoot = this.XamlRoot
                    };

                    var dialogResult = await dialog.ShowAsync();
                    if (dialogResult == ContentDialogResult.Primary && result.DownloadUrl != null)
                    {
                        await Windows.System.Launcher.LaunchUriAsync(new Uri(result.DownloadUrl));
                    }
                    return;
                }
                else
                {
                    dialog = new ContentDialog
                    {
                        Title = "检查更新",
                        Content = "已是最新版本。",
                        CloseButtonText = "确定",
                        XamlRoot = this.XamlRoot
                    };
                }

                await dialog.ShowAsync();
            }
            catch (Exception ex)
            {
                var dialog = new ContentDialog
                {
                    Title = "检查更新",
                    Content = $"检查更新时发生错误：{ex.Message}",
                    CloseButtonText = "确定",
                    XamlRoot = this.XamlRoot
                };
                await dialog.ShowAsync();
            }
            finally
            {
                UpdateCheckButton.IsEnabled = true;
                UpdateCheckButton.Content = "检查";
            }
        }

        private void AnimateButton(Microsoft.UI.Xaml.UIElement target, float scale, int zOffset)
        {
            var visual = ElementCompositionPreview.GetElementVisual(target);
            var compositor = visual.Compositor;
            visual.Properties.InsertVector3("Translation", new System.Numerics.Vector3(0, 0, zOffset));
            var sx = compositor.CreateScalarKeyFrameAnimation();
            sx.InsertKeyFrame(1.0f, scale); sx.Duration = TimeSpan.FromMilliseconds(200);
            var sy = compositor.CreateScalarKeyFrameAnimation();
            sy.InsertKeyFrame(1.0f, scale); sy.Duration = TimeSpan.FromMilliseconds(200);
            visual.StartAnimation("Scale.X", sx);
            visual.StartAnimation("Scale.Y", sy);
        }
    }
}
