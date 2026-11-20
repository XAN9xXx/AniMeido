using Uno.UI.Composition.Drawing;
using Uno.UI.Composition.Skia;
using Windows.UI.Text;

namespace AniMeido.App.Services;

// Uno Skia 只解析一个字体来源。界面统一选择衬线体时，将其缺字交给随包中文字体。
internal sealed class DesktopFontProvider(string fontDirectory) : IFontProvider
{
    internal static DesktopFontProvider Shared { get; } = new(Path.Combine(AppContext.BaseDirectory, "Assets", "Fonts"));
    private readonly IFontProvider _native = SkiaBackend.CreateFontProvider();
    private readonly Lazy<Task<(IFontFile General, IFontFile Reading)>> _files = new(() => Task.Run(() =>
    {
        var provider = SkiaBackend.CreateFontProvider();
        using var general = File.OpenRead(Path.Combine(fontDirectory, "DesktopWenKai.ttf"));
        using var reading = File.OpenRead(Path.Combine(fontDirectory, "DesktopWenKaiReading.ttf"));
        return (provider.LoadFontFile(general), provider.LoadFontFile(reading));
    }));
    private readonly Dictionary<(float Size, bool Reading), IFont?> _chinese = new();
    private readonly object _gate = new();

    internal bool UseSerif { get; set; }

    public IFont? CreateFont(byte[] data, string? familyNameHint, FontWeight weight, FontStretch stretch, FontStyle style, float fontSize)
        => _native.CreateFont(data, familyNameHint, weight, stretch, style, fontSize);

    public IFontFile LoadFontFile(Stream stream) => _native.LoadFontFile(stream);

    public IFont? MatchFamily(string familyName, FontWeight weight, FontStretch stretch, FontStyle style, float fontSize)
        => _native.MatchFamily(familyName, weight, stretch, style, fontSize);

    public ValueTask<IFont?> MatchFamilyAsync(string familyName, FontWeight weight, FontStretch stretch, FontStyle style, float fontSize)
        => _native.MatchFamilyAsync(familyName, weight, stretch, style, fontSize);

    public IFont GetDefaultFont(FontWeight weight, FontStretch stretch, FontStyle style, float fontSize)
        => _native.GetDefaultFont(weight, stretch, style, fontSize);

    public async ValueTask<IFont?> MatchCharacterAsync(int codepoint, FontWeight weight, FontStretch stretch, FontStyle style, float fontSize)
    {
        if (UseSerif)
        {
            var files = await _files.Value.ConfigureAwait(false);
            lock (_gate)
            {
                // 17px 使用已审阅的 VTT 小字版；缺字和其余字号使用完整自动对齐版。
                if (Math.Abs(fontSize - 17) < .01f && GetChinese(files.Reading, fontSize, true) is { } reading
                    && reading.ContainsGlyph(codepoint))
                    return reading;
                if (GetChinese(files.General, fontSize, false) is { } general && general.ContainsGlyph(codepoint))
                    return general;
            }
        }
        return await _native.MatchCharacterAsync(codepoint, weight, stretch, style, fontSize).ConfigureAwait(false);
    }

    private IFont? GetChinese(IFontFile file, float size, bool reading)
    {
        var key = (size, reading);
        if (_chinese.TryGetValue(key, out var font))
            return font;
        // 用户缩放可能产生连续字号，限制缓存数量；字形文件仍由 IFontFile 共用。
        if (_chinese.Count >= 256)
            _chinese.Clear();
        font = file.CreateFont(null, new FontWeight { Weight = 400 }, FontStretch.Normal, FontStyle.Normal, size);
        _chinese.Add(key, font);
        return font;
    }
}
