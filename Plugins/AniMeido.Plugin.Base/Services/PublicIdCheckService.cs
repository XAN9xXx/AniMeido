using AniMeido.Plugin.Base.Models;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;

namespace AniMeido.Plugin.Base.Services;

public sealed class PublicIdCheckService : IAsyncDisposable
{
    internal const string VersionConfigKey = "public-id-check-version";
    private readonly SqliteConnectionFactory _db;
    private readonly HttpClient _http;
    private readonly MetadataDataSource _metadata;
    private readonly PersonalIdRemapService _remap;
    private readonly ILogger<PublicIdCheckService> _logger;
    private readonly Channel<(string? Version, int? Id)> _queue = Channel.CreateUnbounded<(string?, int?)>();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly SemaphoreSlim _checkGate = new(1, 1);
    private readonly ConcurrentDictionary<int, byte> _queuedDetails = new();
    private Task? _worker;

    public PublicIdCheckService(SqliteConnectionFactory db, HttpClient http, MetadataDataSource metadata,
        PersonalIdRemapService remap, ILogger<PublicIdCheckService> logger)
    { _db = db; _http = http; _metadata = metadata; _remap = remap; _logger = logger; }

    public void Start()
    {
        if (_worker is not null) return;
        _metadata.DataVersionChanged += OnVersionChanged;
        _metadata.MergedDetailObserved += OnMergedDetail;
        _queue.Writer.TryWrite((null, null));
        _worker = Task.Run(ProcessAsync);
    }

    private void OnVersionChanged(string version) => _queue.Writer.TryWrite((version, null));
    private void OnMergedDetail(int id)
    {
        if (_queuedDetails.TryAdd(id, 0) && !_queue.Writer.TryWrite((null, id)))
            _queuedDetails.TryRemove(id, out _);
    }

    private async Task ProcessAsync()
    {
        var attempted = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            await _remap.FlushLogAsync(_lifetime.Token).ConfigureAwait(false);
            await foreach (var request in _queue.Reader.ReadAllAsync(_lifetime.Token).ConfigureAwait(false))
            {
                try
                {
                    var version = request.Version ?? await _metadata.GetCurrentDataVersionAsync(_lifetime.Token).ConfigureAwait(false);
                    if (request.Id is null && !attempted.Add(version)) continue;
                    await CheckAsync(version, request.Id, _lifetime.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { break; }
#pragma warning disable CA1031 // 后台检查失败不影响个人页面；下次启动或新版本再试。
                catch (Exception ex) { _logger.LogWarning(ex, "Public ID background check failed"); }
#pragma warning restore CA1031
                finally
                {
                    if (request.Id is { } id) _queuedDetails.TryRemove(id, out _);
                }
            }
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
    }

    internal async Task CheckAsync(string version, int? requestedId = null, CancellationToken ct = default)
    {
        await _checkGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            MigrationInput input;
            var legacy = new List<(int LocalId, int OldId)>();
            using (var connection = await _db.OpenAsync(ct).ConfigureAwait(false))
            {
                using var command = connection.CreateCommand();
                command.CommandText = "SELECT Value FROM config WHERE Key=@key";
                command.Parameters.AddWithValue("@key", VersionConfigKey);
                if (requestedId is null && await command.ExecuteScalarAsync(ct).ConfigureAwait(false) is string saved && saved == version) return;
                input = await PendingReviewService.ReadRecordsAsync(connection, null, ct).ConfigureAwait(false);
                if (requestedId is null)
                {
                    command.CommandText = "SELECT LocalId,LegacyBangumiId FROM legacy_anime_refs WHERE Reason='not_found' ORDER BY LocalId";
                    using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
                    while (await reader.ReadAsync(ct).ConfigureAwait(false)) legacy.Add((reader.GetInt32(0), reader.GetInt32(1)));
                }
            }
            var mappings = await FetchAsync<MappingStatus>("/v1/mapping/work?source=bangumi&ids=", legacy.Select(r => r.OldId),
                m => MigrationInput.Id(m.SourceId.ValueKind == JsonValueKind.String ? m.SourceId.GetString() : m.SourceId.GetRawText()), ct).ConfigureAwait(false);
            var positive = PendingReviewService.AllIds(input).Where(id => id > 0 && (requestedId is null || requestedId == id));
            var works = await FetchAsync<WorkStatus>("/v1/works?ids=", positive, w => w.Id, ct).ConfigureAwait(false);
            if (mappings.Any(m => m.Status is not ("active" or "merged" or "not_found" or "deactivated")
                || (m.Status is "active" or "merged" && m.PublicId is not > 0))
                || works.Any(w => w.Status == "merged" && (w.MergedInto is not > 0 || w.MergedInto == w.Id)))
                throw new InvalidDataException("资料检查响应中的状态或目标无效。");
            var results = new List<PersonalIdRemapResult>();
            await _remap.RunTransactionAsync(async (connection, tx) =>
            {
                MigrationInput? current = null;
                var currentIds = new HashSet<int>();
                foreach (var mapping in mappings)
                {
                    var old = MigrationInput.Id(mapping.SourceId.ValueKind == JsonValueKind.String ? mapping.SourceId.GetString() : mapping.SourceId.GetRawText());
                    var from = legacy.Single(r => r.OldId == old).LocalId;
                    using var verify = connection.CreateCommand(); verify.Transaction = tx;
                    verify.CommandText = "SELECT COUNT(*) FROM legacy_anime_refs WHERE LocalId=@id AND Reason='not_found'";
                    verify.Parameters.AddWithValue("@id", from);
                    if (Convert.ToInt32(await verify.ExecuteScalarAsync(ct).ConfigureAwait(false)) == 0) continue;
                    if (mapping.Status is "active" or "merged")
                    {
                        await EnsureRecordsAsync().ConfigureAwait(false);
                        if (currentIds.Contains(mapping.PublicId!.Value))
                            await Execute("UPDATE legacy_anime_refs SET Reason='conflict' WHERE LocalId=@id", from).ConfigureAwait(false);
                        else
                        {
                            results.Add(await PersonalIdRemapService.RewriteAsync(connection, tx, from, mapping.PublicId.Value, current!, ct).ConfigureAwait(false));
                            await RefreshRecordsAsync().ConfigureAwait(false);
                        }
                    }
                    else if (mapping.Status == "deactivated")
                        await Execute("UPDATE legacy_anime_refs SET Reason='deactivated' WHERE LocalId=@id", from).ConfigureAwait(false);
                }
                foreach (var work in works)
                {
                    // 正常作品的 status 是放送状态，只有这三个值表示资料问题。
                    if (work.Status is not ("merged" or "deactivated" or "not_found"))
                    {
                        await Execute("DELETE FROM public_id_issues WHERE AnimeId=@id", work.Id).ConfigureAwait(false);
                        continue;
                    }
                    if (work.Status == "not_found")
                    {
                        _logger.LogWarning("Public ID {AnimeId} returned not_found; retaining personal records and issue registration", work.Id);
                        continue;
                    }
                    await EnsureRecordsAsync().ConfigureAwait(false);
                    if (!currentIds.Contains(work.Id)) continue;
                    if (work.Status == "merged" && !currentIds.Contains(work.MergedInto!.Value))
                    {
                        results.Add(await PersonalIdRemapService.RewriteAsync(connection, tx, work.Id, work.MergedInto.Value, current!, ct).ConfigureAwait(false));
                        await RefreshRecordsAsync().ConfigureAwait(false);
                    }
                    else
                    {
                        using var issue = connection.CreateCommand(); issue.Transaction = tx;
                        issue.CommandText = """
                            INSERT INTO public_id_issues(AnimeId,Kind,MergedInto,TitleSnapshot,DetectedAt)
                            VALUES(@id,@kind,@to,@title,@at) ON CONFLICT(AnimeId) DO UPDATE SET
                                Kind=excluded.Kind,MergedInto=excluded.MergedInto,TitleSnapshot=excluded.TitleSnapshot
                            """;
                        issue.Parameters.AddWithValue("@id", work.Id); issue.Parameters.AddWithValue("@kind", work.Status == "merged" ? "merged_conflict" : "deactivated");
                        issue.Parameters.AddWithValue("@to", work.MergedInto ?? (object)DBNull.Value);
                        issue.Parameters.AddWithValue("@title", PendingReviewService.Title(current!, work.Id));
                        issue.Parameters.AddWithValue("@at", DateTimeOffset.UtcNow.ToString("O"));
                        await issue.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                    }
                }
                if (requestedId is null)
                {
                    using var done = connection.CreateCommand(); done.Transaction = tx;
                    done.CommandText = "INSERT INTO config(Key,Value) VALUES(@key,@version) ON CONFLICT(Key) DO UPDATE SET Value=excluded.Value";
                    done.Parameters.AddWithValue("@key", VersionConfigKey); done.Parameters.AddWithValue("@version", version);
                    await done.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }

                async Task Execute(string sql, int id)
                {
                    using var command = connection.CreateCommand(); command.Transaction = tx; command.CommandText = sql;
                    command.Parameters.AddWithValue("@id", id); await command.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
                }

                Task EnsureRecordsAsync() => current is null ? RefreshRecordsAsync() : Task.CompletedTask;

                async Task RefreshRecordsAsync()
                {
                    // 事务内只在首次需要时读取；实际改写后刷新，供后续冲突判断复用。
                    current = await PendingReviewService.ReadRecordsAsync(connection, tx, ct).ConfigureAwait(false);
                    currentIds = PendingReviewService.AllIds(current).ToHashSet();
                }
            }, ct, invalidateCache: mappings.Any(m => m.Status is "active" or "merged")
                || works.Any(w => w.Status != "not_found")).ConfigureAwait(false);
            foreach (var result in results) _remap.Log(result);

            async Task<List<T>> FetchAsync<T>(string prefix, IEnumerable<int> ids, Func<T, int> identity, CancellationToken token)
            {
                var result = new List<T>();
                foreach (var batch in ids.Distinct().Order().Chunk(100))
                {
                    using var response = await _http.GetAsync(prefix + string.Join(',', batch), token).ConfigureAwait(false);
                    response.EnsureSuccessStatusCode();
                    if (!response.Headers.TryGetValues("X-Data-Version", out var values) || values.SingleOrDefault() != version)
                        throw new InvalidDataException("资料版本在检查期间变化，请在下个版本或启动时重试。");
                    var page = JsonSerializer.Deserialize<StatusPage<T>>(await response.Content.ReadAsStringAsync(token).ConfigureAwait(false), PublicIdMigrationService.JsonOptions);
                    if (page is null || page.Items.Count != batch.Length || page.Items.Select(identity).Distinct().Count() != batch.Length
                        || page.Items.Any(item => !batch.Contains(identity(item)))) throw new InvalidDataException("资料检查响应未覆盖全部请求 ID。");
                    result.AddRange(page.Items);
                }
                return result;
            }
        }
        finally { _checkGate.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        _metadata.DataVersionChanged -= OnVersionChanged; _metadata.MergedDetailObserved -= OnMergedDetail;
        _queue.Writer.TryComplete(); _lifetime.Cancel();
        if (_worker is not null) await _worker.ConfigureAwait(false);
        _lifetime.Dispose();
    }

    private sealed record MappingStatus(JsonElement SourceId, string Status, int? PublicId);
    private sealed record WorkStatus(int Id, string? Status, int? MergedInto);
    private sealed class StatusPage<T> { public List<T> Items { get; set; } = []; }
}
