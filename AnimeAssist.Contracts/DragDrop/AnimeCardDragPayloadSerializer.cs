using System.Text.Json;

namespace AniMeido.Contracts.DragDrop;

/// <summary>
/// AnimeCardDragPayload 的 JSON 序列化与反序列化辅助。
/// 反序列化时校验 kind 和 schemaVersion，失败时返回 null 而非抛出异常。
/// </summary>
public static class AnimeCardDragPayloadSerializer
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>将 payload 序列化为 JSON 字符串。</summary>
    public static string Serialize(AnimeCardDragPayload payload)
    {
        return JsonSerializer.Serialize(payload, Options);
    }

    /// <summary>
    /// 从 JSON 字符串反序列化 payload。
    /// 校验 kind == "AniMeido.AnimeCard"，不匹配或解析失败时返回 null。
    /// </summary>
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
