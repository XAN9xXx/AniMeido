using System.Text.Json;

namespace AniMeido.Plugin.Player.Sources.Rules;

internal static class JsonPathReader
{
    public static IReadOnlyList<JsonElement> ReadItems(
        JsonElement root,
        string path)
    {
        var element = Read(root, path);
        return element.ValueKind == JsonValueKind.Array
            ? element.EnumerateArray().ToArray()
            : [element];
    }

    public static string ReadString(JsonElement root, string path)
    {
        var element = Read(root, path);
        return element.ValueKind switch
        {
            JsonValueKind.String => element.GetString() ?? string.Empty,
            JsonValueKind.Number => element.GetRawText(),
            JsonValueKind.True => bool.TrueString,
            JsonValueKind.False => bool.FalseString,
            _ => throw new InvalidDataException(
                $"JSON 路径不是可转换为文本的值：{path}"),
        };
    }

    private static JsonElement Read(JsonElement root, string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path == "$")
        {
            return root;
        }

        var current = root;
        foreach (var segment in path
            .Trim()
            .TrimStart('$')
            .TrimStart('.')
            .Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            if (current.ValueKind == JsonValueKind.Object
                && current.TryGetProperty(segment, out var property))
            {
                current = property;
                continue;
            }

            if (current.ValueKind == JsonValueKind.Array
                && int.TryParse(segment, out var index)
                && index >= 0
                && index < current.GetArrayLength())
            {
                current = current[index];
                continue;
            }

            throw new InvalidDataException($"JSON 路径不存在：{path}");
        }

        return current;
    }
}
