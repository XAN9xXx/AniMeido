using AniMeido.Contracts.PersonalAnime;
using System.Text.Json;

namespace AniMeido.Plugin.AI.Providers;

internal static class ProviderResponseParser
{
    private const string ChangesStart = "<ani-changes>";
    private const string ChangesEnd = "</ani-changes>";
    private static readonly JsonSerializerOptions JsonOptions = new(
        JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    public static (string Text, IReadOnlyList<PersonalAnimeChange> Changes)
        Parse(string text, IEnumerable<string>? toolArguments = null)
    {
        var changes = new List<PersonalAnimeChange>();
        if (toolArguments is not null)
        {
            foreach (var arguments in toolArguments)
            {
                AddChanges(arguments, changes);
            }
        }

        var displayText = text;
        var start = displayText.IndexOf(
            ChangesStart,
            StringComparison.OrdinalIgnoreCase);
        while (start >= 0)
        {
            var end = displayText.IndexOf(
                ChangesEnd,
                start + ChangesStart.Length,
                StringComparison.OrdinalIgnoreCase);
            if (end < 0)
            {
                break;
            }

            AddChanges(
                displayText[(start + ChangesStart.Length)..end],
                changes);
            displayText = string.Concat(
                displayText.AsSpan(0, start),
                displayText.AsSpan(end + ChangesEnd.Length));
            start = displayText.IndexOf(
                ChangesStart,
                StringComparison.OrdinalIgnoreCase);
        }

        var distinctChanges = changes
                .GroupBy(item => item.ChangeId, StringComparer.Ordinal)
                .Select(group => group.First())
                .ToList();
        var normalizedText = displayText.Trim();
        if (normalizedText.Length == 0 && distinctChanges.Count > 0)
        {
            normalizedText = "已生成结构化变更提案，请在右侧逐项审查。";
        }

        return (normalizedText, distinctChanges);
    }

    private static void AddChanges(
        string json,
        ICollection<PersonalAnimeChange> destination)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("changes", out var nested))
            {
                root = nested;
            }

            if (root.ValueKind != JsonValueKind.Array)
            {
                return;
            }

            var parsed = root.Deserialize<List<PersonalAnimeChange>>(
                JsonOptions);
            if (parsed is not null)
            {
                foreach (var item in parsed)
                {
                    destination.Add(item);
                }
            }
        }
        catch (JsonException)
        {
            // Invalid provider tool output remains visible as text and is never applied.
        }
    }
}
