using AniMeido.Plugin.AI.Models;
using System.Text;
using System.Text.Json;

namespace AniMeido.Plugin.AI.Providers;

internal static class ProviderRequestBuilder
{
    private static readonly JsonSerializerOptions JsonOptions = new(
        JsonSerializerDefaults.Web);

    public static string BuildUserText(AiProviderRequest request)
    {
        var builder = new StringBuilder();
        foreach (var message in request.Messages)
        {
            builder.Append(message.Role).Append(": ")
                .AppendLine(message.Body);
        }

        builder.AppendLine("当前授权数据快照：");
        builder.AppendLine(JsonSerializer.Serialize(request.Snapshot, JsonOptions));
        builder.AppendLine("本轮用户请求：");
        builder.Append(request.UserMessage);
        return builder.ToString();
    }

    public static object ChangeSchema() => new
    {
        type = "object",
        properties = new
        {
            changes = new
            {
                type = "array",
                items = new
                {
                    type = "object",
                    properties = new
                    {
                        changeId = new { type = "string" },
                        kind = new { type = "integer", minimum = 0, maximum = 3 },
                        animeId = new { type = "integer" },
                        title = new { type = "string" },
                        reason = new { type = "string" },
                        trackingStatus = new { type = new[] { "integer", "null" } },
                        planPriority = new { type = new[] { "integer", "null" } },
                        planTargetStartDate = new { type = new[] { "string", "null" } },
                        text = new { type = new[] { "string", "null" } },
                        episodeNumber = new { type = new[] { "integer", "null" } },
                        occurredAt = new { type = new[] { "string", "null" } },
                    },
                    required = new[]
                    {
                        "changeId", "kind", "animeId", "title", "reason",
                    },
                    additionalProperties = false,
                },
            },
        },
        required = new[] { "changes" },
        additionalProperties = false,
    };

    public static object ChangeTool() => new
    {
        name = "propose_anime_changes",
        description = "仅提出 AniMeido 结构化变更，绝不直接执行。",
        input_schema = ChangeSchema(),
    };

    public static object ChangeSchemaForGemini() => new
    {
        type = "object",
        properties = new
        {
            changes = new
            {
                type = "array",
                items = new
                {
                    type = "object",
                    properties = new
                    {
                        changeId = new { type = "string" },
                        kind = new { type = "integer" },
                        animeId = new { type = "integer" },
                        title = new { type = "string" },
                        reason = new { type = "string" },
                        trackingStatus = new { type = "integer" },
                        planPriority = new { type = "integer" },
                        planTargetStartDate = new { type = "string" },
                        text = new { type = "string" },
                        episodeNumber = new { type = "integer" },
                        occurredAt = new { type = "string" },
                    },
                    required = new[]
                    {
                        "changeId", "kind", "animeId", "title", "reason",
                    },
                },
            },
        },
        required = new[] { "changes" },
    };
}
