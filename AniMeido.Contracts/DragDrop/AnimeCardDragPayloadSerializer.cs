using System.Text.Json;

namespace AniMeido.Contracts.DragDrop;

/// <summary>
/// AnimeCardDragPayload 的 JSON 序列化工具；反序列化时校验 kind 和 schemaVersion，失败时返回 null 而非抛出异常。
/// </summary>
public static class AnimeCardDragPayloadSerializer
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public static string Serialize(AnimeCardDragPayload payload)
    {
        return JsonSerializer.Serialize(payload, Options);
    }

    public static AnimeCardDragPayload? Deserialize(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return null;

            var hasKind = false;
            var hasSchemaVersion = false;
            foreach (var property in root.EnumerateObject())
            {
                hasKind |= property.Name.Equals("kind", StringComparison.OrdinalIgnoreCase);
                hasSchemaVersion |= property.Name.Equals("schemaVersion", StringComparison.OrdinalIgnoreCase);
            }

            // 出站默认值不能替代入站载荷明确声明的卡片身份。
            if (!hasKind || !hasSchemaVersion)
                return null;

            var payload = root.Deserialize<AnimeCardDragPayload>(Options);
            if (payload == null || payload.AnimeId == 0)
                return null;

            if (!string.Equals(payload.Kind, KnownDragDataFormats.AnimeCardKind, StringComparison.Ordinal))
                return null;

            if (payload.SchemaVersion < 1)
                return null;

            return payload;
        }
        catch (JsonException)
        {
            return null;
        }
        catch (ArgumentNullException)
        {
            return null;
        }
    }
}
