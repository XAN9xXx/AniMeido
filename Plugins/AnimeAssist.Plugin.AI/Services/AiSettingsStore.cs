using AniMeido.Plugin.AI.Models;
using System.Text.Json;

namespace AniMeido.Plugin.AI.Services;

internal sealed class AiSettingsStore
{
    private static readonly JsonSerializerOptions JsonOptions = new(
        JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };
    private readonly AiPluginPaths _paths;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public AiSettingsStore(AiPluginPaths paths)
        => _paths = paths;

    public async Task<AiSettings> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (!File.Exists(_paths.SettingsPath))
            {
                return AiSettings.Default;
            }

            try
            {
                var json = await File.ReadAllTextAsync(
                    _paths.SettingsPath,
                    cancellationToken);
                var settings = JsonSerializer.Deserialize<AiSettings>(
                    json,
                    JsonOptions);
                return settings?.SchemaVersion == AiSettings.CurrentSchemaVersion
                    ? Normalize(settings)
                    : AiSettings.Default;
            }
            catch (JsonException)
            {
                return AiSettings.Default;
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(
        AiSettings settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings = Normalize(settings);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var tempPath = _paths.SettingsPath + ".tmp";
            await File.WriteAllTextAsync(
                tempPath,
                JsonSerializer.Serialize(settings, JsonOptions),
                cancellationToken);
            File.Move(tempPath, _paths.SettingsPath, overwrite: true);
        }
        finally
        {
            _gate.Release();
        }
    }

    private static AiSettings Normalize(AiSettings settings)
        => settings with
        {
            SchemaVersion = AiSettings.CurrentSchemaVersion,
            Model = settings.Model.Trim(),
            BaseUrl = settings.BaseUrl.Trim().TrimEnd('/'),
            TimeoutSeconds = Math.Clamp(settings.TimeoutSeconds, 10, 300),
            MaximumOutputTokens = Math.Clamp(
                settings.MaximumOutputTokens,
                256,
                32_768),
        };
}
