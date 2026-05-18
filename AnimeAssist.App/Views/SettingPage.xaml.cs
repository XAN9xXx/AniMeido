using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml;

namespace AniMeido.App.Views
{
    public sealed partial class SettingPage : Page
    {
        public SettingPage()
        {
            InitializeComponent();

            ThemeToggle.IsOn = App.ThemeService.GetCurrentTheme() == ElementTheme.Dark;
            ThemeToggle.Toggled += (s, e) => App.ThemeService.ToggleTheme();
        }
    }
}
