using AniMeido.Plugin.Base.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using System.Globalization;
using System.Text.Json;

namespace AniMeido.Plugin.Base.Services;

public sealed partial class PublicIdMigrationService
{
    internal static readonly string[] WorkTables =
    [
        "tracking",
        "browse_history",
        "anime_plans",
        "plan_reminders",
        "anime_progress",
        "episode_progress",
        "watch_sessions",
        "anime_archives",
        "archive_entries",
        "anime_personal_tags",
        "screenshots",
        "manual_watch_events",
        "tracking_events",
        "recommendation_hidden_anime",
    ];

    internal const string PlayerMapConfigKey = "public-id-migration-v9";
    internal const string VocabConfigKey = "metadata-tag-names";
    internal static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private readonly SqliteConnectionFactory _db;
    private readonly HttpClient _http;
    private readonly ILogger<PublicIdMigrationService> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public PublicIdMigrationService(
        SqliteConnectionFactory db,
        HttpClient http,
        ILogger<PublicIdMigrationService> logger)
    {
        _db = db;
        _http = http;
        _logger = logger;
    }

    public async Task MigrateAsync(CancellationToken ct = default)
    {
        try
        {
            using var operation = _db.Access.BeginWrite();
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                // SQLite 异步方法仍可能同步读库；整次迁移离开调用线程，资格与排队时点不变。
                await Task.Run(
                    () => MigrateAndPublishPlayerMapAsync(ct),
                    ct).ConfigureAwait(false);
            }
            finally
            {
                _gate.Release();
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Public ID migration failed; retaining records for retry");
            throw;
        }
    }

    private async Task MigrateAndPublishPlayerMapAsync(CancellationToken ct)
    {
        using (var connection = await _db.OpenAsync(ct).ConfigureAwait(false))
        {
            var version = await PersonalDatabaseSchema.GetSchemaVersionAsync(connection).ConfigureAwait(false);
            if (version == PersonalDatabaseSchema.OfflineVersion)
            {
                var input = await ReadInputAsync(connection, ct: ct).ConfigureAwait(false);
                var plan = await PrepareAsync(input, ct).ConfigureAwait(false);
                await ApplyAsync(connection, plan, ct).ConfigureAwait(false);
            }
            else if (version != PersonalDatabaseSchema.CurrentVersion
                && version != PersonalDatabaseSchema.PublicIdVersion)
            {
                throw new InvalidDataException($"无法从数据库版本 {version} 进行公共 ID 迁移。");
            }

            await PersonalDatabaseSchema.RunMigrationsAsync(connection).ConfigureAwait(false);
        }

        await WritePlayerMapAsync(ct).ConfigureAwait(false);
    }

    internal static async Task<MigrationInput> ReadInputAsync(
        SqliteConnection connection,
        SqliteTransaction? tx = null,
        CancellationToken ct = default)
    {
        var input = new MigrationInput();
        foreach (var table in WorkTables.Concat(["saved_tags", "smart_lists", "recommendation_feature_preferences"]))
        {
            using var cmd = connection.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = $"SELECT * FROM {table}";
            using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            var rows = new List<Dictionary<string, string?>>();
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var row = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
                for (var i = 0; i < reader.FieldCount; i++)
                {
                    row[reader.GetName(i)] = reader.IsDBNull(i)
                        ? null
                        : Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture);
                }
                rows.Add(row);
            }
            input.Tables[table] = rows;
        }

        using (var cmd = connection.CreateCommand())
        {
            cmd.Transaction = tx;
            cmd.CommandText = """
                SELECT Data FROM cache WHERE CacheKey LIKE 'detail:%' OR CacheKey LIKE 'season:%'
                    OR CacheKey LIKE 'broadcast:%' OR CacheKey LIKE 'tag_search:%' OR CacheKey LIKE 'person_works:%'
                ORDER BY CacheKey
                """;
            using var reader = await cmd.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                input.CacheJson.Add(reader.GetString(0));
            }
        }

        input.CollectWorks();
        return input;
    }

    internal async Task<PublicIdMigrationPlan> PrepareAsync(MigrationInput input, CancellationToken ct)
    {
        string? dataVersion = null;
        var works = await MapAsync("work", input.Counts.Keys).ConfigureAwait(false);
        var plan = PublicIdMigrationPlan.ResolveWorks(input, works);
        plan.SourceState = input.State();

        var preferences = input.Rows("recommendation_feature_preferences").ToList();
        var participantIds = preferences
            .Where(p => MigrationInput.Id(p.GetValueOrDefault("FeatureKind")) is 1 or 2)
            .Select(p => MigrationInput.Id(p.GetValueOrDefault("FeatureKey")))
            .Where(id => id > 0);
        var participants = (await MapAsync("participant", participantIds).ConfigureAwait(false))
            .ToDictionary(m => m.SourceId);
        var tags = input.Rows("saved_tags")
            .Select(r => r.GetValueOrDefault("TagName") ?? "")
            .ToList();
        var rules = input.Rows("smart_lists").ToList();
        var needsVocab = tags.Count > 0
            || preferences.Any(p => p.GetValueOrDefault("FeatureKind") == "0")
            || rules.Any(r => MetadataTagMigration.TagNames(r.GetValueOrDefault("RuleJson") ?? "").Any())
            || (dataVersion is null && input.Tables.Values.Any(rows => rows.Count > 0));
        var vocabulary = new List<MetadataVocab>();
        if (needsVocab)
        {
            foreach (var category in new[] { "tag", "format", "country", "source_type" })
            {
                var response = await GetAsync<MetadataPage<MetadataVocab>>(
                    $"/v1/vocab?category={category}").ConfigureAwait(false);
                if (response.Items.Count == 0
                    || response.Items.Any(v => v.Category != category
                        || string.IsNullOrWhiteSpace(v.Code)
                        || string.IsNullOrWhiteSpace(v.NameZh)))
                {
                    throw new InvalidDataException("资料服务未返回有效的标签词表。");
                }
                vocabulary.AddRange(response.Items);
            }
        }

        var tagMap = MetadataTagMigration.Load(vocabulary);
        plan.VocabularyNames.UnionWith(vocabulary.Select(v => v.NameZh));
        plan.HasVocabulary = needsVocab;
        plan.Tags.AddRange(tags
            .Select(name => tagMap.Convert(name))
            .GroupBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderBy(t => t.Invalid).First()));
        foreach (var row in rules)
        {
            plan.Rules[row["Id"]!] = tagMap.ConvertRules(row["RuleJson"]!);
        }

        var converted = new List<(MigratedPreference Preference, string OriginalKey)>();
        foreach (var row in preferences)
        {
            var kind = MigrationInput.Id(row["FeatureKind"]);
            var key = row["FeatureKey"]!;
            var name = row["DisplayName"]!;
            var invalid = true;
            if (kind == (int)RecommendationFeatureKind.Tag)
            {
                var tag = tagMap.Convert(name);
                if (tag.Invalid)
                {
                    tag = tagMap.Convert(key);
                }
                invalid = tag.Invalid;
                if (!invalid)
                {
                    key = RecommendationCandidateProvider.NormalizeTag(tag.Name);
                    name = tag.Name;
                }
            }
            else if (participants.TryGetValue(MigrationInput.Id(key), out var mapping) && mapping.PublicId is > 0)
            {
                key = mapping.PublicId.Value.ToString(CultureInfo.InvariantCulture);
                invalid = false;
            }
            if (kind is (int)RecommendationFeatureKind.Studio or (int)RecommendationFeatureKind.VoiceActor && invalid)
            {
                key = "legacy:bangumi:" + row["FeatureKey"];
            }
            converted.Add((
                new MigratedPreference(
                    kind,
                    key,
                    name,
                    MigrationInput.Id(row["Adjustment"]),
                    row["UpdatedAt"]!,
                    invalid),
                row["FeatureKey"]!));
        }

        foreach (var group in converted.GroupBy(p => (p.Preference.Kind, p.Preference.Key.ToUpperInvariant())))
        {
            var ordered = group
                .OrderBy(p => p.Preference.Invalid)
                .ThenBy(p => string.Equals(p.OriginalKey, p.Preference.Key, StringComparison.OrdinalIgnoreCase) ? 0 : 1)
                .ThenByDescending(p => p.Preference.UpdatedAt, StringComparer.Ordinal)
                .ToList();
            plan.Preferences.Add(ordered[0].Preference);
            foreach (var other in ordered.Skip(1))
            {
                plan.Preferences.Add(other.Preference with
                {
                    Key = other.Preference.Kind == (int)RecommendationFeatureKind.Tag
                        ? other.OriginalKey
                        : "legacy:bangumi:" + other.OriginalKey,
                    Invalid = true,
                });
            }
        }
        return plan;

        async Task<List<IdMapping>> MapAsync(string entity, IEnumerable<int> ids)
        {
            var result = new List<IdMapping>();
            foreach (var batch in ids.Distinct().Order().Chunk(100))
            {
                var response = await GetAsync<MappingResponse>(
                    $"/v1/mapping/{entity}?source=bangumi&ids={string.Join(',', batch)}").ConfigureAwait(false);
                var mapped = response.Items
                    .Select(item => new IdMapping(
                        MigrationInput.Id(item.SourceId.ValueKind == JsonValueKind.String
                            ? item.SourceId.GetString()
                            : item.SourceId.GetRawText()),
                        item.Status is "active" or "merged" ? item.PublicId : null,
                        item.Status))
                    .ToList();
                if (mapped.Count != batch.Length
                    || mapped.Select(m => m.SourceId).Distinct().Count() != batch.Length
                    || mapped.Any(m => !batch.Contains(m.SourceId)
                        || m.Status is not ("active" or "merged" or "not_found" or "deactivated")
                        || (m.Status is "active" or "merged" && m.PublicId is not > 0)))
                {
                    throw new InvalidDataException("映射接口未返回完整有效的逐项结果。");
                }
                result.AddRange(mapped);
            }
            return result;
        }

        async Task<T> GetAsync<T>(string url)
        {
            using var response = await _http.GetAsync(url, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            var version = response.Headers.TryGetValues("X-Data-Version", out var values)
                ? values.SingleOrDefault()
                : null;
            if (string.IsNullOrWhiteSpace(version) || (dataVersion is not null && dataVersion != version))
            {
                throw new InvalidDataException("资料版本在换算期间变化或响应缺少数据版本，请重试。");
            }
            dataVersion = version;
            return JsonSerializer.Deserialize<T>(
                await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false),
                JsonOptions)
                ?? throw new InvalidDataException("资料服务返回空响应。");
        }
    }

    private sealed class MappingResponse
    {
        public List<MappingItem> Items { get; set; } = [];
    }

    private sealed record MappingItem(JsonElement SourceId, int? PublicId, string Status);
}
