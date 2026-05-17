using Microsoft.UI.Xaml;
using Windows.Storage;

namespace AnimeAssist.App.Services
{
    public class ThemeService
    {
        private FrameworkElement? _rootElement;
        public void InitializeTheme(FrameworkElement rootElement)
        {
            _rootElement = rootElement;
            var localSettings = ApplicationData.Current.LocalSettings;
            string? themeSetting = localSettings.Values["ThemeSetting"] as string;

            if (Enum.TryParse<ElementTheme>(themeSetting, out var theme))
                _rootElement.RequestedTheme = theme;
        }

        public void ToggleTheme()
        {
            var current = _rootElement.RequestedTheme;
            if (current == ElementTheme.Dark)
            {
                _rootElement.RequestedTheme = ElementTheme.Light;
            }
            else
            {
                _rootElement.RequestedTheme = ElementTheme.Dark;
            }

            ApplicationData.Current.LocalSettings.Values["ThemeSetting"] = _rootElement.RequestedTheme.ToString();
        }

        public ElementTheme GetCurrentTheme()
        {
            return _rootElement.RequestedTheme;
        }
    }
}
