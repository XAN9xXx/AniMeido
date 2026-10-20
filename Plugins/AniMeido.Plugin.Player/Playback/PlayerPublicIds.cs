using System.Globalization;
using System.Text.Json;

namespace AniMeido.Plugin.Player.Playback;

internal static class PlayerPublicIds
{
    internal static string SharedMigrationPath => Path.Combine(Environment.GetFolderPath(
        Environment.SpecialFolder.ApplicationData), "AniMeido", "id-migration-v9.json");

    internal static async Task<Dictionary<int, int>?> ReadAsync(string? path, CancellationToken ct)
    {
        if (path is null || !File.Exists(path)) return null;
        var file = JsonSerializer.Deserialize<MigrationFile>(await File.ReadAllTextAsync(path, ct),
            new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? throw new InvalidDataException("Player 公共 ID 迁移文件无效。");
        if (file.SchemaVersion != 9 || file.WorkIds is null
            || file.WorkIds.Any(p => p.Key <= 0 || p.Value == 0 || (p.Value < 0 && p.Value != -p.Key)))
            throw new InvalidDataException("Player 公共 ID 迁移文件版本或对照表无效。");
        return file.WorkIds;
    }

    internal static Dictionary<string, string> Rewrite(Dictionary<string, string> values, Dictionary<int, int> ids, bool sourceKeys)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var item in values)
        {
            var parts = sourceKeys ? item.Key.Split(':', 2) : [item.Key];
            var key = item.Key;
            if (int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var old) && old > 0)
            {
                // Base 不知道的 Player 专有记录也不能继续冒充公共 ID，保留为负数本地引用。
                var id = ids.GetValueOrDefault(old, -old);
                key = id.ToString(CultureInfo.InvariantCulture) + (sourceKeys && parts.Length == 2 ? ":" + parts[1] : "");
            }
            if (!result.TryAdd(key, item.Value)) throw new InvalidDataException("Player ID 迁移存在重复源映射，未覆盖原记录。");
        }
        return result;
    }

    private sealed record MigrationFile(int SchemaVersion, Dictionary<int, int>? WorkIds);
}
