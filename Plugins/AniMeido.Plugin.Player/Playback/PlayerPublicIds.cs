using System.Globalization;
using System.Text.Json;

namespace AniMeido.Plugin.Player.Playback;

internal static class PlayerPublicIds
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    internal static string SharedMigrationPath => Path.Combine(AppDataRoots.Roaming, "id-migration-v9.json");
    internal static string SharedRemapPath => Path.Combine(Path.GetDirectoryName(SharedMigrationPath)!, "id-remap-log.json");

    internal sealed record RemapEntry(long Seq, int From, int To, string At);

    internal static async Task<List<RemapEntry>> ReadRemapsAsync(
        string? path, long afterSequence, CancellationToken ct)
    {
        if (path is null || !File.Exists(path))
            return [];

        await using var stream = File.OpenRead(path);
        var rows = await JsonSerializer.DeserializeAsync<List<RemapEntry>>(stream, SerializerOptions, ct)
            .ConfigureAwait(false) ?? throw new InvalidDataException("Player ID 改写日志无效。");
        for (var index = 0; index < rows.Count; index++)
        {
            var row = rows[index];
            if (row is null || row.Seq != index + 1L || row.From == 0 || row.To == 0 || row.From == row.To)
                throw new InvalidDataException("Player ID 改写日志序号或 ID 无效。");
        }
        return rows.Where(row => row.Seq > afterSequence).ToList();
    }

    internal static Dictionary<string, string> ApplyRemaps(
        Dictionary<string, string> values,
        IEnumerable<RemapEntry> entries,
        bool sourceKeys,
        Action<RemapEntry, IReadOnlyDictionary<string, string>>? beforeRemap = null)
    {
        var result = new Dictionary<string, string>(values, StringComparer.Ordinal);
        foreach (var entry in entries)
        {
            beforeRemap?.Invoke(entry, result);
            foreach (var key in result.Keys.Where(key => MatchesAnimeId(key, entry.From, sourceKeys)).ToList())
            {
                var suffix = sourceKeys && key.Contains(':') ? key[key.IndexOf(':')..] : "";
                var value = result[key];
                result.Remove(key);
                result.TryAdd(entry.To.ToString(CultureInfo.InvariantCulture) + suffix, value);
            }
        }
        return result;
    }

    private static bool MatchesAnimeId(string key, int animeId, bool sourceKeys)
    {
        var idText = key.AsSpan();
        if (sourceKeys)
        {
            var separator = idText.IndexOf(':');
            if (separator >= 0)
                idText = idText[..separator];
        }
        return int.TryParse(idText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id)
            && id == animeId;
    }

    internal static async Task<Dictionary<int, int>?> ReadAsync(string? path, CancellationToken ct)
    {
        if (path is null || !File.Exists(path)) return null;
        var file = JsonSerializer.Deserialize<MigrationFile>(await File.ReadAllTextAsync(path, ct),
            SerializerOptions) ?? throw new InvalidDataException("Player 公共 ID 迁移文件无效。");
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
