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
            var payload = JsonSerializer.Deserialize<AnimeCardDragPayload>(json, Options);
            if (payload == null)
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
