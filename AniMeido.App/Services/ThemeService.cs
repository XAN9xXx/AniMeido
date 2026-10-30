using Microsoft.UI.Xaml;

namespace AniMeido.App.Services;

public sealed class ThemeService(DesktopSettingsStore settings) : IAsyncDisposable
{
    private readonly SemaphoreSlim _changes = new(1, 1);
    private FrameworkElement? _rootElement;
    private Task? _disposeTask;
    private ElementTheme _theme = ElementTheme.Default;

    /// <summary>主题应用完成后通知标题栏等非绑定控件。</summary>
    public event EventHandler<ElementTheme>? ThemeChanged;

    public async Task InitializeThemeAsync(FrameworkElement rootElement)
    {
        ObjectDisposedException.ThrowIf(_disposeTask is not null, this);
        await _changes.WaitAsync();
        try
        {
            var saved = await settings.LoadAsync();
            _rootElement = rootElement;
            Apply(ParseTheme(saved.ThemeSetting));
        }
        finally
        {
            _changes.Release();
        }
    }

    public async Task SetThemeAsync(ElementTheme theme)
    {
        ObjectDisposedException.ThrowIf(_disposeTask is not null, this);
        await _changes.WaitAsync();
        try
        {
            await settings.UpdateAsync(value => value with { ThemeSetting = theme.ToString() });
            Apply(theme);
        }
        finally
        {
            _changes.Release();
        }
    }

    public ElementTheme GetCurrentTheme() => _theme;

    public ValueTask DisposeAsync() => new(_disposeTask ??= DetachAsync());

    private async Task DetachAsync()
    {
        await _changes.WaitAsync();
        try
        {
            _rootElement = null;
            ThemeChanged = null;
        }
        finally
        {
            _changes.Release();
        }
    }

    private void Apply(ElementTheme theme)
    {
        _theme = theme;
        if (_rootElement is not null)
        {
            _rootElement.RequestedTheme = theme;
        }
        ThemeChanged?.Invoke(this, theme);
    }

    private static ElementTheme ParseTheme(string? value)
        => value switch
        {
            "Light" => ElementTheme.Light,
            "Dark" => ElementTheme.Dark,
            _ => ElementTheme.Default,
        };
}
