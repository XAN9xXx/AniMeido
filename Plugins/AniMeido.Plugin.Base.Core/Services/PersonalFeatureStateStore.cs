using AniMeido.Contracts.Models;
using AniMeido.Plugin.Base.Models;
using Microsoft.Data.Sqlite;
using System.Text.Json;

namespace AniMeido.Plugin.Base.Services;

public sealed class PersonalFeatureStateStore
{
    private readonly SqliteConnectionFactory _dbFactory;
    private readonly SemaphoreSlim _configWriteGate = new(1, 1);
    private static readonly JsonSerializerOptions ConfigJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public PersonalFeatureStateStore(SqliteConnectionFactory dbFactory)
    {
        _dbFactory = dbFactory;
    }

    public async Task SaveDragZoneConfigAsync(List<DragZoneConfig> configs)
    {
        using var operation = _dbFactory.Access.BeginWrite();
        var json = JsonSerializer.Serialize(configs, ConfigJsonOptions);

        await SaveConfigValueAsync("drag_zones", json).ConfigureAwait(false);
    }

    public Task<List<DragZoneConfig>> LoadDragZoneConfigAsync()
        => Task.Run(async () =>
        {
            using var connection = await _dbFactory.OpenAsync().ConfigureAwait(false);
            return await LoadDragZoneConfigAsync(connection, null).ConfigureAwait(false);
        });

    internal static async Task<List<DragZoneConfig>> LoadDragZoneConfigAsync(
        SqliteConnection connection, SqliteTransaction? transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT Value FROM config WHERE Key = 'drag_zones'";
        var result = await command.ExecuteScalarAsync();

        if (result is string json && !string.IsNullOrEmpty(json))
        {
            var zones = JsonSerializer.Deserialize<List<DragZoneConfig>>(json, ConfigJsonOptions)
                ?? DragZoneConfig.GetDefaults();
            return zones;
        }

        return DragZoneConfig.GetDefaults();
    }

    private const string CalendarDailyPickKey = "calendar_daily_pick";

    private sealed record CalendarDailyPickState(string Date, int AnimeId);

    /// <summary>读取某天抽到的作品；没有记录或内容无法解析时返回 null。</summary>
    public Task<(DateOnly Date, int AnimeId)?> LoadCalendarDailyPickAsync()
        => Task.Run<(DateOnly Date, int AnimeId)?>(async () =>
        {
            using var connection = await _dbFactory.OpenAsync().ConfigureAwait(false);

            using var command = connection.CreateCommand();
            command.CommandText = "SELECT Value FROM config WHERE Key = @key";
            command.Parameters.AddWithValue("@key", CalendarDailyPickKey);
            if (await command.ExecuteScalarAsync().ConfigureAwait(false) is not string json
                || string.IsNullOrEmpty(json))
            {
                return null;
            }

            try
            {
                var state = JsonSerializer.Deserialize<CalendarDailyPickState>(json, ConfigJsonOptions);
                return state is not null
                    && DateOnly.TryParseExact(
                        state.Date,
                        "yyyy-MM-dd",
                        System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.None,
                        out var date)
                    ? (date, state.AnimeId)
                    : null;
            }
            catch (JsonException)
            {
                return null;
            }
        });

    /// <summary>记下某天抽到的作品，重启后同一天仍显示它。</summary>
    public async Task SaveCalendarDailyPickAsync(DateOnly date, int animeId)
    {
        using var operation = _dbFactory.Access.BeginWrite();
        var json = JsonSerializer.Serialize(
            new CalendarDailyPickState(
                date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
                animeId),
            ConfigJsonOptions);

        await SaveConfigValueAsync(CalendarDailyPickKey, json).ConfigureAwait(false);
    }

    private const string TimeMachineShownKey = "time_machine_shown";

    private sealed record TimeMachineShownJson(int Year, string Season, IReadOnlyList<int> AnimeIds);

    /// <summary>
    /// 读取番剧时光机每一季显示过的作品（跨重启），用于下次优先抽没显示过的；
    /// 没有记录或无法解析时返回空。
    /// </summary>
    public Task<IReadOnlyDictionary<(int Year, Season Season), IReadOnlyList<int>>> LoadTimeMachineShownAsync()
        => Task.Run<IReadOnlyDictionary<(int Year, Season Season), IReadOnlyList<int>>>(async () =>
        {
            using var connection = await _dbFactory.OpenAsync().ConfigureAwait(false);

            using var command = connection.CreateCommand();
            command.CommandText = "SELECT Value FROM config WHERE Key = @key";
            command.Parameters.AddWithValue("@key", TimeMachineShownKey);
            if (await command.ExecuteScalarAsync().ConfigureAwait(false) is not string json
                || string.IsNullOrEmpty(json))
            {
                return new Dictionary<(int Year, Season Season), IReadOnlyList<int>>();
            }

            try
            {
                return (JsonSerializer.Deserialize<List<TimeMachineShownJson>>(json, ConfigJsonOptions) ?? [])
                    .Where(item => Enum.TryParse<Season>(item.Season, out var season) && Enum.IsDefined(season))
                    .GroupBy(item => (item.Year, Season: Enum.Parse<Season>(item.Season)))
                    .ToDictionary(
                        group => group.Key,
                        group => (IReadOnlyList<int>)group.SelectMany(item => item.AnimeIds ?? []).Distinct().ToList());
            }
            catch (JsonException)
            {
                return new Dictionary<(int Year, Season Season), IReadOnlyList<int>>();
            }
        });

    public async Task SaveTimeMachineShownAsync(IReadOnlyDictionary<(int Year, Season Season), IReadOnlyList<int>> shown)
    {
        using var operation = _dbFactory.Access.BeginWrite();
        var json = JsonSerializer.Serialize(
            shown
                .OrderBy(pair => pair.Key.Year)
                .ThenBy(pair => pair.Key.Season)
                .Select(pair => new TimeMachineShownJson(pair.Key.Year, pair.Key.Season.ToString(), pair.Value))
                .ToList(),
            ConfigJsonOptions);

        await SaveConfigValueAsync(TimeMachineShownKey, json).ConfigureAwait(false);
    }

    private const string TodayThemeKey = "today_theme";

    private sealed record TodayThemeStateJson(
        string Date,
        string Theme,
        bool Fallback,
        IReadOnlyList<int> AnimeIds);

    /// <summary>读取今天页“今日主题”记下的主题与这一批作品；没有记录或无法解析时返回 null。</summary>
    public Task<TodayThemeState?> LoadTodayThemeAsync()
        => Task.Run<TodayThemeState?>(async () =>
        {
            using var connection = await _dbFactory.OpenAsync().ConfigureAwait(false);

            using var command = connection.CreateCommand();
            command.CommandText = "SELECT Value FROM config WHERE Key = @key";
            command.Parameters.AddWithValue("@key", TodayThemeKey);
            if (await command.ExecuteScalarAsync().ConfigureAwait(false) is not string json
                || string.IsNullOrEmpty(json))
            {
                return null;
            }

            try
            {
                var state = JsonSerializer.Deserialize<TodayThemeStateJson>(json, ConfigJsonOptions);
                return state is not null
                    && DateOnly.TryParseExact(
                        state.Date,
                        "yyyy-MM-dd",
                        System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.None,
                        out var date)
                    && Enum.TryParse<TodayThemeKind>(state.Theme, out var theme)
                    && Enum.IsDefined(theme)
                    ? new TodayThemeState(date, theme, state.Fallback, state.AnimeIds ?? [])
                    : null;
            }
            catch (JsonException)
            {
                return null;
            }
        });

    /// <summary>记下今天显示的主题与这一批作品，重启后同一天按它恢复。</summary>
    public async Task SaveTodayThemeAsync(TodayThemeState state)
    {
        using var operation = _dbFactory.Access.BeginWrite();
        var json = JsonSerializer.Serialize(
            new TodayThemeStateJson(
                state.Date.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
                state.Theme.ToString(),
                state.IsFallback,
                state.AnimeIds),
            ConfigJsonOptions);

        await SaveConfigValueAsync(TodayThemeKey, json).ConfigureAwait(false);
    }

    private const string RecentSearchesKey = "search_recent";

    /// <summary>读取搜索页的最近搜索词（新的在前）；没有记录或无法解析时返回空。</summary>
    public Task<IReadOnlyList<string>> LoadRecentSearchesAsync()
        => Task.Run<IReadOnlyList<string>>(async () =>
        {
            using var connection = await _dbFactory.OpenAsync().ConfigureAwait(false);

            using var command = connection.CreateCommand();
            command.CommandText = "SELECT Value FROM config WHERE Key = @key";
            command.Parameters.AddWithValue("@key", RecentSearchesKey);
            if (await command.ExecuteScalarAsync().ConfigureAwait(false) is not string json
                || string.IsNullOrEmpty(json))
            {
                return [];
            }

            try
            {
                return JsonSerializer.Deserialize<List<string>>(json, ConfigJsonOptions)?
                    .Where(query => !string.IsNullOrWhiteSpace(query))
                    .ToList() ?? [];
            }
            catch (JsonException)
            {
                return [];
            }
        });

    /// <summary>保存最近搜索词；传入空列表即清除。</summary>
    public async Task SaveRecentSearchesAsync(IReadOnlyList<string> queries)
    {
        using var operation = _dbFactory.Access.BeginWrite();
        var json = JsonSerializer.Serialize(queries, ConfigJsonOptions);

        await SaveConfigValueAsync(RecentSearchesKey, json).ConfigureAwait(false);
    }

    private async Task SaveConfigValueAsync(string key, string json)
    {
        // 先按调用顺序排队，避免后台调度把较早的快照写到新选择之后。
        await _configWriteGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await Task.Run(async () =>
            {
                using var connection = await _dbFactory.OpenAsync().ConfigureAwait(false);
                using var command = connection.CreateCommand();
                command.CommandText = """
                    INSERT OR REPLACE INTO config (Key, Value)
                    VALUES (@key, @value)
                    """;
                command.Parameters.AddWithValue("@key", key);
                command.Parameters.AddWithValue("@value", json);
                await command.ExecuteNonQueryAsync().ConfigureAwait(false);
            }).ConfigureAwait(false);
        }
        finally
        {
            _configWriteGate.Release();
        }
    }
}
