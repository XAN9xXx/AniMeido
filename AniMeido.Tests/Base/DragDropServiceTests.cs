using AniMeido.Contracts.Models;
using AniMeido.Contracts.Notifications;
using AniMeido.Plugin.Base.Services;
using Microsoft.Extensions.Logging;

namespace AniMeido.Tests;

public sealed class DragDropServiceTests : DbTestBase
{
    private readonly PersonalFeatureStateStore _featureState;

    public DragDropServiceTests()
    {
        _featureState = new PersonalFeatureStateStore(DbFactory);
    }

    private readonly DropLogger _logger = new();
    private readonly List<string> _messages = [];

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DropPreservesTheResultWithoutShowingAnError(bool routed)
    {
        var service = CreateService();

        var result = await service.HandleDropAsync(() => Task.FromResult(routed), ShowFailureAsync);

        Assert.Equal(routed, result);
        Assert.Empty(_messages);
        Assert.Empty(_logger.Entries);
    }

    [Fact]
    public async Task SuccessfulDropPersistsTheStatusWithoutAnError()
    {
        await RunProductionMigrationAsync();
        var tracking = new TrackingService(DbFactory);
        var service = new DragDropService(tracking, _featureState, _logger);

        var result = await service.HandleDropAsync(async () =>
        {
            await tracking.SetStatusAsync(42, AnimeTrackingStatus.Watching);
            return true;
        }, ShowFailureAsync);

        Assert.True(result);
        Assert.Equal(AnimeTrackingStatus.Watching, await tracking.GetStatusAsync(42));
        Assert.Empty(_messages);
        Assert.Empty(_logger.Entries);
    }

    [Fact]
    public async Task BusyDropShowsTheBusyMessageWithoutLoggingAnError()
    {
        await RunProductionMigrationAsync();
        var tracking = new TrackingService(DbFactory);
        var service = new DragDropService(tracking, _featureState, _logger);
        bool result = true;
        using (DbFactory.Access.BeginExclusive())
        {
            await PersonalDataAccessTests.OutsideOperation(async () =>
            {
                result = await service.HandleDropAsync(async () =>
                {
                    await tracking.SetStatusAsync(42, AnimeTrackingStatus.Watching);
                    return true;
                }, ShowFailureAsync);
            });
        }

        Assert.False(result);
        Assert.Null(await tracking.GetStatusAsync(42));
        Assert.Equal(new PersonalDataBusyException().Message, Assert.Single(_messages));
        Assert.Empty(_logger.Entries);
    }

    [Fact]
    public async Task FailureAfterCommitDoesNotRetryTheAlreadySavedStatus()
    {
        await RunProductionMigrationAsync();
        var notifications = new FailingNotifications();
        var tracking = new TrackingService(DbFactory, notifications);
        var service = new DragDropService(tracking, _featureState, _logger);

        var result = await service.HandleDropAsync(async () =>
        {
            await tracking.SetStatusAsync(42, AnimeTrackingStatus.Watching);
            return true;
        }, ShowFailureAsync);

        Assert.False(result);
        Assert.Equal(AnimeTrackingStatus.Watching, await tracking.GetStatusAsync(42));
        Assert.Equal(1, notifications.Attempts);
        Assert.Equal("拖放未完成，请刷新后确认状态，再重试。", Assert.Single(_messages));
        Assert.Equal(LogLevel.Error, Assert.Single(_logger.Entries).Level);
    }

    [Fact]
    public async Task StorageFailureIsHandledOnceWithoutClaimingThatNothingWasSaved()
    {
        var tracking = new TrackingService(DbFactory);
        var service = new DragDropService(tracking, _featureState, _logger);

        var result = await service.HandleDropAsync(async () =>
        {
            // 没有初始化表，使用真实服务触发写入失败。
            await tracking.SetStatusAsync(42, AnimeTrackingStatus.Watching);
            return true;
        }, ShowFailureAsync);

        Assert.False(result);
        Assert.Equal("拖放未完成，请刷新后确认状态，再重试。", Assert.Single(_messages));
        var entry = Assert.Single(_logger.Entries);
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.IsType<Microsoft.Data.Sqlite.SqliteException>(entry.Exception);
    }

    [Fact]
    public async Task UnexpectedCancellationIsNotReportedAsAUserCancellation()
    {
        var service = CreateService();
        var failure = new OperationCanceledException();

        var result = await service.HandleDropAsync(() => throw failure, ShowFailureAsync);

        Assert.False(result);
        Assert.Single(_messages);
        Assert.Same(failure, Assert.Single(_logger.Entries).Exception);
    }

    [Fact]
    public async Task FeedbackFailureDoesNotEscapeOrRunTheDropAgain()
    {
        var service = CreateService();
        var failure = new IOException("drop failed");
        var feedbackFailure = new InvalidOperationException("page closed");
        var attempts = 0;

        var result = await service.HandleDropAsync(() =>
        {
            attempts++;
            throw failure;
        }, _ => throw feedbackFailure);

        Assert.False(result);
        Assert.Equal(1, attempts);
        Assert.Collection(_logger.Entries,
            entry =>
            {
                Assert.Equal(LogLevel.Error, entry.Level);
                Assert.Same(failure, entry.Exception);
            },
            entry =>
            {
                Assert.Equal(LogLevel.Warning, entry.Level);
                Assert.Same(feedbackFailure, entry.Exception);
            });
    }

    private DragDropService CreateService() => new(new TrackingService(DbFactory), _featureState, _logger);

    private Task ShowFailureAsync(string message)
    {
        _messages.Add(message);
        return Task.CompletedTask;
    }

    private sealed class FailingNotifications : IAppNotificationService
    {
        public bool IsSupported => true;
        public bool NotificationsEnabled => true;
        public int Attempts { get; private set; }

        public Task CancelGroupAsync(string group, CancellationToken cancellationToken = default)
        {
            Attempts++;
            throw new IOException("fixture notification failure");
        }

        public Task ScheduleAsync(AppNotificationRequest request, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public Task CancelAsync(string group, string tag, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
        public IDisposable RegisterActivationHandler(string category,
            Func<AppNotificationActivation, CancellationToken, Task> handler)
            => throw new NotSupportedException();
        public Task OpenNotificationSettingsAsync() => throw new NotSupportedException();
    }

    private sealed class DropLogger : ILogger<DragDropService>
    {
        public List<(LogLevel Level, Exception? Exception)> Entries { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Entries.Add((logLevel, exception));
        }
    }
}
