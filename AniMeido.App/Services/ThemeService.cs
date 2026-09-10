using Microsoft.UI.Xaml;

namespace AniMeido.App.Services
{
    public class ThemeService
    {
        private FrameworkElement? _rootElement;
        private const string RegistryKey = @"HKEY_CURRENT_USER\Software\AniMeido";

        /// <summary>主题切换后触发，供标题栏等非 XAML 绑定控件同步。</summary>
        public event EventHandler<ElementTheme>? ThemeChanged;

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
            if (_rootElement is null) return;
            if (_rootElement.RequestedTheme == theme) return; // 相同主题不重复应用
            _rootElement.RequestedTheme = theme;
            Microsoft.Win32.Registry.SetValue(
                RegistryKey, "ThemeSetting", theme.ToString());
            ThemeChanged?.Invoke(this, theme);
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
