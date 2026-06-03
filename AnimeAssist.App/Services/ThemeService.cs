using Microsoft.UI.Xaml;

namespace AniMeido.App.Services
{
    public class ThemeService
    {
        private FrameworkElement? _rootElement;
        private const string RegistryKey = @"HKEY_CURRENT_USER\Software\AniMeido";

        public void InitializeTheme(FrameworkElement rootElement)
        {
            _rootElement = rootElement;
            var themeSetting = Microsoft.Win32.Registry.GetValue(
                RegistryKey, "ThemeSetting", null) as string;

            if (Enum.TryParse<ElementTheme>(themeSetting, out var theme))
                _rootElement.RequestedTheme = theme;
        }

        public void SetTheme(ElementTheme theme)
        {
            if (_rootElement is not null)
                _rootElement.RequestedTheme = theme;
            Microsoft.Win32.Registry.SetValue(
                RegistryKey, "ThemeSetting", theme.ToString());
        }

        public ElementTheme GetCurrentTheme()
        {
            return _rootElement?.RequestedTheme ?? Application.Current.RequestedTheme switch
            {
                ApplicationTheme.Light => ElementTheme.Light,
                ApplicationTheme.Dark => ElementTheme.Dark,
                _ => ElementTheme.Default,
            };
        }
    }
}
