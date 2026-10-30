using AniMeido.Contracts;
using AniMeido.Contracts.Models;
using AniMeido.Plugin.Base.Exceptions;
using AniMeido.Plugin.Base.Services;
using AniMeido.Plugin.Base.ViewModels;
using Microsoft.Extensions.Logging;
using System.Collections.Concurrent;
using System.Text.Json;

namespace AniMeido.Tests;

public sealed class AnimeDetailLoggingTests : DbTestBase
{
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(10);
    private const int FirstId = -101;
    private const int CurrentId = -202;

    [Theory]
    [InlineData("network")]
    [InlineData("metadata")]
    [InlineData("timeout")]
    [InlineData("state")]
    [InlineData("json")]
    public async Task Logging_CurrentDetailFailureIsOneErrorWithTheOriginalException(string failure)
    {
        var error = Failure(failure);
        var source = new DetailSource
        {
            Detail = (_, _) => Task.FromException<Anime?>(error),
        };
        var logger = new DetailLogger();
        var model = Model(source, logger);

        await model.LoadDetailCommand.ExecuteAsync(FirstId).WaitAsync(Budget);

        AssertEntry(logger, LogLevel.Error, error, FirstId);
        Assert.True(model.IsError);
        Assert.False(model.HasData);
        Assert.False(model.IsLoading);
    }

    [Theory]
    [InlineData("network")]
    [InlineData("metadata")]
    [InlineData("timeout")]
    [InlineData("state")]
    [InlineData("json")]
    public async Task Logging_APreviousSelectionsDetailFailureDoesNotProduceALog(string failure)
    {
        await CreateBaseTablesAsync();
        var pending = NewCompletion<Anime?>();
        var started = NewCompletion();
        var source = new DetailSource
        {
            Detail = (id, _) =>
            {
                if (id != FirstId)
                    return Task.FromResult<Anime?>(CreateAnime(id));
                started.TrySetResult();
                return pending.Task;
            },
        };
        var logger = new DetailLogger();
        var model = Model(source, logger);
        Task first = Task.CompletedTask;
        Task current = Task.CompletedTask;

        try
        {
            first = model.LoadDetailCommand.ExecuteAsync(FirstId);
            await started.Task.WaitAsync(Budget);
            current = model.LoadDetailCommand.ExecuteAsync(CurrentId);
            await current.WaitAsync(Budget);
            pending.TrySetException(Failure(failure));
            await first.WaitAsync(Budget);

            Assert.Empty(logger.Entries);
            Assert.Equal(CurrentId, model.AnimeDetail?.ID);
            Assert.True(model.HasData);
            Assert.False(model.IsError);
        }
        finally
        {
            pending.TrySetResult(null);
            await ObserveAsync(first, current).WaitAsync(Budget);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Logging_ExplicitDetailCancellationDoesNotProduceALog(bool taskCancellation)
    {
        var pending = NewCompletion<Anime?>();
        var started = NewCompletion();
        var token = CancellationToken.None;
        var source = new DetailSource
        {
            Detail = (_, cancellationToken) =>
            {
                token = cancellationToken;
                started.TrySetResult();
                return pending.Task;
            },
        };
        var logger = new DetailLogger();
        var model = Model(source, logger);
        Task loading = Task.CompletedTask;

        try
        {
            loading = model.LoadDetailCommand.ExecuteAsync(FirstId);
            await started.Task.WaitAsync(Budget);
            model.LoadDetailCommand.Cancel();
            Assert.True(token.IsCancellationRequested);
            pending.TrySetException(taskCancellation
                ? new TaskCanceledException("主动取消", null, token)
                : new OperationCanceledException(token));
            await loading.WaitAsync(Budget);

            Assert.Empty(logger.Entries);
            Assert.False(model.IsError);
            Assert.False(model.IsLoading);
        }
        finally
        {
            pending.TrySetResult(null);
            await ObserveAsync(loading).WaitAsync(Budget);
        }
    }

    [Theory]
    [InlineData("studios", "network")]
    [InlineData("studios", "metadata")]
    [InlineData("studios", "json")]
    [InlineData("characters", "network")]
    [InlineData("characters", "metadata")]
    [InlineData("characters", "json")]
    public async Task Logging_OptionalDetailFailureIsOneWarningAndKeepsTheMainDetailUsable(string part, string failure)
    {
        await CreateBaseTablesAsync();
        var source = new DetailSource();
        var extra = new ExtraGate(source, part);
        var logger = new DetailLogger();
        var model = Model(source, logger);
        var error = Failure(failure);
        Task loading = Task.CompletedTask;

        try
        {
            loading = model.LoadDetailCommand.ExecuteAsync(FirstId);
            await extra.Started.Task.WaitAsync(Budget);
            extra.Fail(error);
            await loading.WaitAsync(Budget);

            AssertEntry(logger, LogLevel.Warning, error, FirstId);
            Assert.Equal(FirstId, model.AnimeDetail?.ID);
            Assert.True(model.HasData);
            Assert.False(model.IsError);
            Assert.False(model.IsLoading);
            Assert.Null(model.StudiosText);
            Assert.Empty(model.Characters);
        }
        finally
        {
            extra.Complete();
            await ObserveAsync(loading).WaitAsync(Budget);
        }
    }

    [Fact]
    public async Task Logging_NormalEmptyOptionalDetailsDoNotProduceALog()
    {
        await CreateBaseTablesAsync();
        var logger = new DetailLogger();
        var model = Model(new DetailSource(), logger);

        await model.LoadDetailCommand.ExecuteAsync(FirstId).WaitAsync(Budget);

        Assert.Empty(logger.Entries);
        Assert.True(model.HasData);
        Assert.False(model.IsError);
        Assert.Null(model.StudiosText);
        Assert.Empty(model.Characters);
    }

    [Theory]
    [InlineData("studios")]
    [InlineData("characters")]
    public async Task Logging_OptionalTimeoutWithoutExplicitCancellationStillUsesTheExistingMainErrorBoundary(string part)
    {
        await CreateBaseTablesAsync();
        var source = new DetailSource();
        var extra = new ExtraGate(source, part);
        var logger = new DetailLogger();
        var model = Model(source, logger);
        var error = new TaskCanceledException("附加资料请求超时");
        Task loading = Task.CompletedTask;

        try
        {
            loading = model.LoadDetailCommand.ExecuteAsync(FirstId);
            await extra.Started.Task.WaitAsync(Budget);
            Assert.False(extra.Token.IsCancellationRequested);
            extra.Fail(error);
            await loading.WaitAsync(Budget);

            AssertEntry(logger, LogLevel.Error, error, FirstId);
            Assert.True(model.IsError);
            Assert.Equal("网络请求超时，请检查网络后重试", model.ErrorMessage);
        }
        finally
        {
            extra.Complete();
            await ObserveAsync(loading).WaitAsync(Budget);
        }
    }

    [Theory]
    [InlineData("studios", "network")]
    [InlineData("studios", "metadata")]
    [InlineData("studios", "json")]
    [InlineData("characters", "network")]
    [InlineData("characters", "metadata")]
    [InlineData("characters", "json")]
    public async Task Logging_APreviousSelectionsOptionalFailureDoesNotProduceALog(string part, string failure)
    {
        await CreateBaseTablesAsync();
        var source = new DetailSource();
        var extra = new ExtraGate(source, part);
        var logger = new DetailLogger();
        var model = Model(source, logger);
        Task first = Task.CompletedTask;
        Task current = Task.CompletedTask;

        try
        {
            first = model.LoadDetailCommand.ExecuteAsync(FirstId);
            await extra.Started.Task.WaitAsync(Budget);
            current = model.LoadDetailCommand.ExecuteAsync(CurrentId);
            await current.WaitAsync(Budget);
            extra.Fail(Failure(failure));
            await first.WaitAsync(Budget);

            Assert.Empty(logger.Entries);
            Assert.Equal(CurrentId, model.AnimeDetail?.ID);
            Assert.True(model.HasData);
            Assert.False(model.IsError);
        }
        finally
        {
            extra.Complete();
            await ObserveAsync(first, current).WaitAsync(Budget);
        }
    }

    [Theory]
    [InlineData("studios")]
    [InlineData("characters")]
    public async Task Logging_ExplicitOptionalCancellationDoesNotProduceALog(string part)
    {
        await CreateBaseTablesAsync();
        var source = new DetailSource();
        var extra = new ExtraGate(source, part);
        var logger = new DetailLogger();
        var model = Model(source, logger);
        Task loading = Task.CompletedTask;

        try
        {
            loading = model.LoadDetailCommand.ExecuteAsync(FirstId);
            await extra.Started.Task.WaitAsync(Budget);
            model.LoadDetailCommand.Cancel();
            Assert.True(extra.Token.IsCancellationRequested);
            extra.Fail(new OperationCanceledException(extra.Token));
            await loading.WaitAsync(Budget);

            Assert.Empty(logger.Entries);
            Assert.False(model.IsError);
            Assert.False(model.IsLoading);
        }
        finally
        {
            extra.Complete();
            await ObserveAsync(loading).WaitAsync(Budget);
        }
    }

    private AnimeDetailViewModel Model(DetailSource source, DetailLogger logger)
        // 负数合成作品不读取评分源；本批只验证详情及附加资料的最终日志边界。
        => new(source, new TrackingService(DbFactory), null!, logger);

    private static void AssertEntry(DetailLogger logger, LogLevel level, Exception error, int animeId)
    {
        var entry = Assert.Single(logger.Entries);
        Assert.Equal(level, entry.Level);
        Assert.Same(error, entry.Exception);
        Assert.Contains(animeId.ToString(System.Globalization.CultureInfo.InvariantCulture), entry.Message);
    }

    private static Anime CreateAnime(int animeId)
        => new(animeId, $"合成作品{animeId}", null, [], null, null, "完整简介", 0, 0);

    private static Exception Failure(string kind) => kind switch
    {
        "network" => new HttpRequestException("合成网络失败"),
        "metadata" => new MetadataServiceException("合成资料失败"),
        "timeout" => new TaskCanceledException("合成请求超时"),
        "state" => new InvalidOperationException("合成状态失败"),
        "json" => new JsonException("合成格式失败"),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static TaskCompletionSource<T> NewCompletion<T>()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static TaskCompletionSource NewCompletion()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static async Task ObserveAsync(params Task[] tasks)
    {
        // 正常断言负责判断操作结果；清理仍等待实际任务，不吞外层的等待预算。
        await Task.WhenAll(tasks).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
    }

    private sealed record LogEntry(LogLevel Level, Exception? Exception, string Message);

    private sealed class DetailLogger : ILogger
    {
        public ConcurrentQueue<LogEntry> Entries { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Warning;
        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(level))
                Entries.Enqueue(new LogEntry(level, exception, formatter(state, exception)));
        }
    }

    private sealed class ExtraGate
    {
        private readonly bool _loadsStudios;
        private readonly TaskCompletionSource<List<Studio>> _studios = NewCompletion<List<Studio>>();
        private readonly TaskCompletionSource<List<CharacterRole>> _characters = NewCompletion<List<CharacterRole>>();
        public TaskCompletionSource Started { get; } = NewCompletion();
        public CancellationToken Token { get; private set; }

        public ExtraGate(DetailSource source, string part)
        {
            if (part == "studios")
            {
                _loadsStudios = true;
                source.Studios = (id, token) =>
                {
                    if (id != FirstId)
                        return Task.FromResult(new List<Studio>());
                    Token = token;
                    Started.TrySetResult();
                    return _studios.Task;
                };
            }
            else if (part == "characters")
            {
                source.Characters = (id, token) =>
                {
                    if (id != FirstId)
                        return Task.FromResult(new List<CharacterRole>());
                    Token = token;
                    Started.TrySetResult();
                    return _characters.Task;
                };
            }
            else
            {
                throw new ArgumentOutOfRangeException(nameof(part));
            }
        }

        public void Fail(Exception error)
        {
            if (_loadsStudios)
                _studios.TrySetException(error);
            else
                _characters.TrySetException(error);
        }

        public void Complete()
        {
            _studios.TrySetResult([]);
            _characters.TrySetResult([]);
        }
    }

    private sealed class DetailSource : IAnimeDataSource
    {
        public Func<int, CancellationToken, Task<Anime?>> Detail { get; init; }
            = (id, _) => Task.FromResult<Anime?>(CreateAnime(id));
        public Func<int, CancellationToken, Task<List<Studio>>> Studios { get; set; }
            = (_, _) => Task.FromResult(new List<Studio>());
        public Func<int, CancellationToken, Task<List<CharacterRole>>> Characters { get; set; }
            = (_, _) => Task.FromResult(new List<CharacterRole>());

        public Task<Anime?> GetAnimeDetailAsync(int animeID, CancellationToken ct) => Detail(animeID, ct);
        public Task<List<Studio>> GetStudioAsync(int animeID, CancellationToken ct) => Studios(animeID, ct);
        public Task<List<CharacterRole>> GetCharacterRolesAsync(int animeID, CancellationToken ct) => Characters(animeID, ct);
        public Task<List<Anime>> GetAnimeBySeasonAsync(int year, Season season, CancellationToken ct) => throw new NotSupportedException();
        public Task<List<Anime>> GetCurrentBroadcastScheduleAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<List<Tag>> GetTagsAsync(int animeID, CancellationToken ct) => throw new NotSupportedException();
        public Task<List<VoiceActor>> GetCVsAsync(int animeID, CancellationToken ct) => throw new NotSupportedException();
        public Task<List<PersonWork>> GetPersonWorksAsync(int personId, CancellationToken ct) => throw new NotSupportedException();
        public Task<(List<Anime> Results, int Total)> SearchByTagAsync(string tag, int offset, string sort, CancellationToken ct, string? airDateFrom = null, string? airDateTo = null) => throw new NotSupportedException();
        public Task<(List<Anime> Results, int Total, int NextOffset)> SearchByKeywordAsync(string keyword, int offset, CancellationToken ct) => throw new NotSupportedException();
    }
}
