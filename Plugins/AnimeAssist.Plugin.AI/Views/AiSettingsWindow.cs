using AniMeido.Plugin.AI.Models;
using AniMeido.Plugin.AI.Providers;
using AniMeido.Plugin.AI.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;

namespace AniMeido.Plugin.AI.Views;

internal sealed class AiSettingsWindow : Window
{
    private readonly AiSettingsStore _settingsStore;
    private readonly DpapiSecretStore _secretStore;
    private readonly AiProviderRouter _router;
    private readonly ComboBox _provider = new();
    private readonly AutoSuggestBox _model = new();
    private readonly TextBox _baseUrl = new();
    private readonly PasswordBox _apiKey = new();
    private readonly ToggleSwitch _webTools = new();
    private readonly NumberBox _timeout = new();
    private readonly NumberBox _maxTokens = new();
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };

    public AiSettingsWindow(
        AiSettingsStore settingsStore,
        DpapiSecretStore secretStore,
        AiProviderRouter router)
    {
        _settingsStore = settingsStore;
        _secretStore = secretStore;
        _router = router;
        Title = "AniMeido AI 插件设置";
        DpiWindowSizing.Resize(this, 720, 760);
        Content = BuildContent();
        Activated += OnActivated;
    }

    private UIElement BuildContent()
    {
        foreach (var descriptor in AiProviderCatalog.All)
        {
            _provider.Items.Add(new ComboBoxItem
            {
                Content = descriptor.DisplayName,
                Tag = descriptor.Kind,
            });
        }

        _provider.SelectionChanged += OnProviderChanged;
        _baseUrl.PlaceholderText = "请填写 Provider 官方 Base URL";
        _model.PlaceholderText = "输入模型 ID，或先连接获取模型列表";
        _apiKey.PlaceholderText = "仅使用 DPAPI 保存到当前 Windows 用户";
        _webTools.Header = "允许厂商联网工具";
        _webTools.OffContent = "关闭（默认）";
        _webTools.OnContent = "发送前会在预览中披露";
        _timeout.Header = "请求超时（秒）";
        _timeout.Minimum = 10;
        _timeout.Maximum = 300;
        _maxTokens.Header = "最大输出 Token";
        _maxTokens.Minimum = 256;
        _maxTokens.Maximum = 32768;

        var save = new Button { Content = "保存设置" };
        save.Click += OnSaveClick;
        var test = new Button { Content = "连接并获取模型" };
        test.Click += OnTestClick;
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
        };
        buttons.Children.Add(save);
        buttons.Children.Add(test);

        var panel = new StackPanel
        {
            Spacing = 12,
            Padding = new Thickness(28),
        };
        panel.Children.Add(new TextBlock
        {
            Text = "AI 插件设置",
            Style = Application.Current.Resources["TitleTextBlockStyle"] as Style,
        });
        panel.Children.Add(new TextBlock
        {
            Text = "密钥仅保存在本机；提示词、个人档案和密钥不会写入普通日志。",
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.75,
        });
        panel.Children.Add(Labeled("Provider", _provider));
        panel.Children.Add(Labeled("模型", _model));
        panel.Children.Add(Labeled("Base URL", _baseUrl));
        panel.Children.Add(new TextBlock
        {
            Text = "地址必须由你确认并输入；灰色占位文字只用于提示常见格式，不会自动保存。",
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.62,
        });
        panel.Children.Add(Labeled("API Key", _apiKey));
        panel.Children.Add(_webTools);
        panel.Children.Add(_timeout);
        panel.Children.Add(_maxTokens);
        panel.Children.Add(buttons);
        panel.Children.Add(_status);
        panel.Children.Add(new TextBlock
        {
            Text = "Provider 官方文档",
            Opacity = 0.65,
        });
        var policyLinks = new StackPanel
        {
            Orientation = Orientation.Vertical,
            Spacing = 8,
        };
        foreach (var descriptor in AiProviderCatalog.All)
        {
            policyLinks.Children.Add(PolicyLink(
                descriptor.DisplayName,
                descriptor.DocumentationUrl));
        }
        panel.Children.Add(policyLinks);
        return new ScrollViewer { Content = panel };
    }

    private static HyperlinkButton PolicyLink(string label, string uri)
        => new()
        {
            Content = label,
            NavigateUri = new Uri(uri),
        };

    private static FrameworkElement Labeled(string title, FrameworkElement input)
    {
        var panel = new StackPanel { Spacing = 6 };
        panel.Children.Add(new TextBlock { Text = title });
        panel.Children.Add(input);
        return panel;
    }

    private async void OnActivated(object sender, WindowActivatedEventArgs args)
    {
        Activated -= OnActivated;
        try
        {
            var settings = await _settingsStore.LoadAsync();
            _provider.SelectedItem = _provider.Items
                .OfType<ComboBoxItem>()
                .FirstOrDefault(item => Equals(item.Tag, settings.Provider));
            _model.Text = settings.Model;
            _baseUrl.Text = settings.BaseUrl;
            _apiKey.Password = _secretStore.LoadApiKey(settings.Provider)
                ?? string.Empty;
            _webTools.IsOn = settings.AllowProviderWebTools;
            _timeout.Value = settings.TimeoutSeconds;
            _maxTokens.Value = settings.MaximumOutputTokens;
        }
        catch (Exception ex) when (
            ex is IOException
                or UnauthorizedAccessException
                or System.ComponentModel.Win32Exception)
        {
            _status.Text = $"加载设置失败：{ex.Message}";
        }
    }

    private void OnProviderChanged(object sender, SelectionChangedEventArgs args)
    {
        if (_provider.SelectedItem is not ComboBoxItem { Tag: AiProviderKind kind })
        {
            return;
        }

        _baseUrl.Text = string.Empty;
        _model.Text = string.Empty;
        _model.ItemsSource = null;
        _baseUrl.PlaceholderText = "请从 Provider 官方文档复制 Base URL";

        _apiKey.Password = _secretStore.LoadApiKey(
            kind,
            includeLegacyKey: false) ?? string.Empty;
        _webTools.IsEnabled = _router.Get(kind).Capabilities.SupportsWebTools;
        if (!_webTools.IsEnabled)
        {
            _webTools.IsOn = false;
        }
    }

    private async void OnSaveClick(object sender, RoutedEventArgs args)
    {
        try
        {
            var settings = ReadSettings();
            await _settingsStore.SaveAsync(settings);
            _secretStore.SaveApiKey(settings.Provider, _apiKey.Password);
            _status.Text = "设置已保存。切换 Provider 或模型后请新建会话。";
        }
        catch (Exception ex) when (
            ex is ArgumentException
                or IOException
                or InvalidOperationException
                or UnauthorizedAccessException
                or System.ComponentModel.Win32Exception)
        {
            _status.Text = ex.Message;
        }
    }

    private async void OnTestClick(object sender, RoutedEventArgs args)
    {
        try
        {
            var settings = ReadSettings(requireModel: false);
            _status.Text = "正在连接…";
            var models = await _router.Get(settings.Provider).GetModelsAsync(
                settings,
                _apiKey.Password,
                CancellationToken.None);
            var choices = models
                .Where(model => !string.IsNullOrWhiteSpace(model))
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.Ordinal)
                .ToArray();
            _model.ItemsSource = choices;
            if (choices.Length == 0)
            {
                _status.Text = "连接成功，但端点未返回模型列表；请手工填写模型 ID。";
            }
            else
            {
                _status.Text = $"连接成功，已加载 {choices.Length} 个模型；请选择或继续手工输入。";
                _model.Focus(FocusState.Programmatic);
                _model.IsSuggestionListOpen = true;
            }
        }
        catch (Exception ex) when (
            ex is HttpRequestException
                or AiProviderException
                or OperationCanceledException
                or InvalidOperationException
                or UnauthorizedAccessException
                or System.ComponentModel.Win32Exception)
        {
            _status.Text = ex.Message;
        }
    }

    private AiSettings ReadSettings(bool requireModel = true)
    {
        if (_provider.SelectedItem is not ComboBoxItem { Tag: AiProviderKind kind })
        {
            throw new InvalidOperationException("请选择 Provider。");
        }

        if ((requireModel && string.IsNullOrWhiteSpace(_model.Text))
            || string.IsNullOrWhiteSpace(_baseUrl.Text)
            || string.IsNullOrWhiteSpace(_apiKey.Password))
        {
            throw new InvalidOperationException(requireModel
                ? "模型、Base URL 和 API Key 均不能为空。"
                : "Base URL 和 API Key 均不能为空。");
        }

        var baseUrlText = _baseUrl.Text.Trim().TrimEnd('/');
        if (!Uri.TryCreate(baseUrlText, UriKind.Absolute, out var baseUri)
            || (!string.Equals(baseUri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
                && !(string.Equals(baseUri.Scheme, Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase)
                    && baseUri.IsLoopback))
            || !string.IsNullOrEmpty(baseUri.Query)
            || !string.IsNullOrEmpty(baseUri.Fragment))
        {
            throw new InvalidOperationException(
                "Base URL 必须是 HTTPS 地址；仅 localhost/回环地址允许使用 HTTP，且不能包含查询参数或片段。");
        }

        return new AiSettings(
            AiSettings.CurrentSchemaVersion,
            kind,
            requireModel ? _model.Text : "connection-test",
            baseUrlText,
            _webTools.IsOn,
            (int)_timeout.Value,
            (int)_maxTokens.Value);
    }
}
