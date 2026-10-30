using AniMeido.Contracts.Playback;
using AniMeido.Plugin.Base.Models;
using AniMeido.Plugin.Base.Services;
using Microsoft.Data.Sqlite;

namespace AniMeido.Tests;

public sealed class ActionCenterServiceTests : IAsyncLifetime
{
    private readonly PersonalDatabaseFixture _database = new();
    private SqliteConnectionFactory DbFactory => _database.Factory;
    private string ConnectionString => $"Data Source={DbFactory.DatabasePath}";

    public Task InitializeAsync() => _database.InitializeAsync();
    public Task DisposeAsync() => _database.DisposeAsync();

    [Fact]
    public async Task RecentReminders_CompareInstantsAcrossOffsetsAndKeepExactWindowBoundaries()
    {
        var service = new ActionCenterService(DbFactory);
        await service.UpsertPlanAsync(42, "时区提醒", AnimePlanPriority.Normal, null, 0);
        var now = new DateTimeOffset(2026, 10, 30, 2, 0, 0, TimeSpan.Zero);
        var lower = now.AddHours(-1);
        var east = TimeSpan.FromHours(8);
        var west = TimeSpan.FromHours(-8);
        PlanReminder[] reminders =
        [
            Absolute("before-lower", 42, lower.AddTicks(-1).ToOffset(east)),
            Absolute("lower", 42, lower.ToOffset(east)),
            Absolute("inside", 42, now.AddMinutes(-30).ToOffset(west)),
            Absolute("upper-offset", 42, now.ToOffset(west)),
            Absolute("upper-utc", 42, now),
            Absolute("after-upper", 42, now.AddTicks(1).ToOffset(west)),
            Absolute("future-local-clock-inside", 42, now.AddHours(7).AddMinutes(30).ToOffset(west)),
            Absolute("old-local-clock-inside", 42, now.AddHours(-8).AddMinutes(-30).ToOffset(east)),
            Absolute("handled", 42, now.AddMinutes(-15).ToOffset(east)) with
            {
                State = PlanReminderState.Handled,
                HandledAt = now,
            },
            Absolute("cancelled", 42, now.AddMinutes(-15).ToOffset(west)) with
            {
                State = PlanReminderState.Cancelled,
            },
            Absolute("caught-up", 42, now.AddMinutes(-15).ToOffset(east)) with
            {
                CatchUpSentAt = now,
            },
        ];
        foreach (var reminder in reminders)
            await service.AddReminderAsync(reminder);

        var recent = await service.GetRecentUnprocessedRemindersAsync(now, TimeSpan.FromHours(1));

        Assert.Equal(new[] { "inside", "lower", "upper-offset", "upper-utc" },
            recent.Select(item => item.ReminderId).Order(StringComparer.Ordinal));
        Assert.Equal(lower.UtcTicks, Assert.Single(recent, item => item.ReminderId == "lower").ScheduledFor.UtcTicks);
        Assert.Equal(now.UtcTicks, Assert.Single(recent, item => item.ReminderId == "upper-offset").ScheduledFor.UtcTicks);
        Assert.Equal(now.UtcTicks, Assert.Single(recent, item => item.ReminderId == "upper-utc").ScheduledFor.UtcTicks);
        Assert.Equal(east, Assert.Single(recent, item => item.ReminderId == "lower").ScheduledFor.Offset);
        Assert.Equal(west, Assert.Single(recent, item => item.ReminderId == "upper-offset").ScheduledFor.Offset);
    }

    [Fact]
    public async Task ReminderList_OrdersByInstantAndPreservesEqualInstantRecordsAndOffsets()
    {
        var service = new ActionCenterService(DbFactory);
        await service.UpsertPlanAsync(42, "时区顺序", AnimePlanPriority.Normal, null, 0);
        await service.UpsertPlanAsync(43, "其他作品", AnimePlanPriority.Normal, null, 0);
        var middle = new DateTimeOffset(2026, 10, 30, 2, 0, 0, TimeSpan.Zero);
        PlanReminder[] reminders =
        [
            Absolute("late-east", 42, middle.AddMinutes(30).ToOffset(TimeSpan.FromHours(8))),
            Absolute("early", 42, middle.AddMinutes(-30)),
            Absolute("middle-west", 42, middle.ToOffset(TimeSpan.FromHours(-8))),
            Absolute("middle-east", 42, middle.ToOffset(TimeSpan.FromHours(8))),
            Absolute("late-utc", 42, middle.AddMinutes(30)),
        ];
        foreach (var reminder in reminders)
            await service.AddReminderAsync(reminder);
        await service.AddReminderAsync(Absolute("other-anime", 43, middle));
        await service.AddReminderAsync(Absolute("handled", 42, middle) with
        {
            State = PlanReminderState.Handled,
            HandledAt = middle,
        });

        var listed = await service.GetRemindersAsync(42, PlanReminderState.Pending);

        Assert.Equal(new[]
        {
            middle.AddMinutes(-30).UtcTicks,
            middle.UtcTicks,
            middle.UtcTicks,
            middle.AddMinutes(30).UtcTicks,
            middle.AddMinutes(30).UtcTicks,
        }, listed.Select(item => item.ScheduledFor.UtcTicks));
        Assert.Equal(reminders.Select(item => item.ReminderId).Order(StringComparer.Ordinal),
            listed.Select(item => item.ReminderId).Order(StringComparer.Ordinal));
        foreach (var original in reminders)
        {
            var saved = Assert.Single(listed, item => item.ReminderId == original.ReminderId);
            Assert.Equal(original, saved);
            Assert.Equal(original.ScheduledFor.Offset, saved.ScheduledFor.Offset);
        }
    }

    [Fact]
    public async Task PlanAndReminderMappings_RoundTrip()
    {
        var service = new ActionCenterService(DbFactory);
        await service.UpsertPlanAsync(
            50,
            "映射测试",
            AnimePlanPriority.High,
            new DateOnly(2026, 8, 1),
            3);
        var scheduledFor = DateTimeOffset.UtcNow.AddDays(1);
        var reminder = new PlanReminder(
            "mapping-reminder",
            50,
            PlanReminderKind.Absolute,
            null,
            null,
            scheduledFor,
            scheduledFor,
            PlanReminderState.Pending,
            null,
            null);
        await service.AddReminderAsync(reminder);

        var plan = await service.GetPlanAsync(50);
        var plans = await service.GetPlansAsync();
        var reminders = await service.GetRemindersAsync(animeId: 50);

        Assert.NotNull(plan);
        Assert.Equal(AnimePlanPriority.High, plan.Priority);
        Assert.Equal(new DateOnly(2026, 8, 1), plan.TargetStartDate);
        Assert.Contains(plans, item => item.AnimeId == 50);
        Assert.Equal(reminder, Assert.Single(reminders));
    }

    [Fact]
    public async Task CancelReminder_UsesCancelledState()
    {
        var service = new ActionCenterService(DbFactory);
        await service.UpsertPlanAsync(60, "取消提醒", AnimePlanPriority.Normal, null, 0);
        var scheduledFor = DateTimeOffset.UtcNow.AddDays(1);
        await service.AddReminderAsync(new PlanReminder(
            "cancel-reminder",
            60,
            PlanReminderKind.Absolute,
            null,
            null,
            scheduledFor,
            scheduledFor,
            PlanReminderState.Pending,
            null,
            null));

        await service.CancelReminderAsync("cancel-reminder");

        var reminder = Assert.Single(
            await service.GetRemindersAsync(animeId: 60));
        Assert.Equal(PlanReminderState.Cancelled, reminder.State);

        await service.CancelReminderAsync("cancel-reminder");
        Assert.Equal(reminder, Assert.Single(await service.GetRemindersAsync(animeId: 60)));
    }

    [Fact]
    public async Task CancelReminder_MissingReminderIsANoOp()
    {
        var service = new ActionCenterService(DbFactory);

        await service.CancelReminderAsync("missing-reminder");

        Assert.Empty(await service.GetRemindersAsync());
        Assert.Empty(await service.GetPlansAsync());
    }

    [Fact]
    [Trait("Category", "Mechanism")]
    public async Task RecordAsync_DeduplicatesAndDoesNotRegressEpisode()
    {
        var service = new ActionCenterService(DbFactory);
        var observedAt = DateTimeOffset.UtcNow;
        var completed = new AnimePlaybackProgress(
            "event-1",
            100,
            2,
            540,
            600,
            false,
            observedAt);

        await service.RecordAsync(completed);
        await service.RecordAsync(completed with { EpisodeNumber = 3 });
        await service.RecordAsync(completed with
        {
            EventId = "event-2",
            EpisodeNumber = 1,
            ObservedAt = observedAt.AddMinutes(1),
        });

        var progress = await service.GetProgressAsync();
        Assert.Equal(2, progress[100].CurrentEpisode);
        using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT COUNT(*) FROM watch_sessions WHERE AnimeId = 100";
        Assert.Equal(2, Convert.ToInt32(await command.ExecuteScalarAsync()));
    }

    [Fact]
    [Trait("Category", "Mechanism")]
    public async Task RecordAsync_NaturalEndCompletesEpisodeBelowNinetyPercent()
    {
        var service = new ActionCenterService(DbFactory);
        var observedAt = DateTimeOffset.UtcNow;
        await service.RecordAsync(new AnimePlaybackProgress(
            "natural-end",
            200,
            1,
            400,
            600,
            true,
            observedAt));

        var snapshot = (await service.GetProgressAsync())[200];
        Assert.Equal(1, snapshot.CurrentEpisode);
        Assert.Equal(400, snapshot.PositionSeconds);
        Assert.Equal(600, snapshot.DurationSeconds);
        Assert.Equal(observedAt, snapshot.LastWatchedAt);

        using var connection = new SqliteConnection(ConnectionString);
        await connection.OpenAsync();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT IsCompleted
            FROM episode_progress
            WHERE AnimeId = 200 AND EpisodeNumber = 1
            """;
        Assert.Equal(1, Convert.ToInt32(await command.ExecuteScalarAsync()));
    }

    [Fact]
    public async Task RecordAsync_DelayedSameEpisodeEventDoesNotRegressSnapshot()
    {
        var service = new ActionCenterService(DbFactory);
        var observedAt = DateTimeOffset.UtcNow;
        await service.RecordAsync(new AnimePlaybackProgress(
            "newer-position",
            300,
            4,
            900,
            1200,
            false,
            observedAt));

        await service.RecordAsync(new AnimePlaybackProgress(
            "delayed-position",
            300,
            4,
            300,
            1000,
            false,
            observedAt.AddMinutes(-5)));

        var snapshot = (await service.GetProgressAsync())[300];
        Assert.Equal(4, snapshot.CurrentEpisode);
        Assert.Equal(900, snapshot.PositionSeconds);
        Assert.Equal(1200, snapshot.DurationSeconds);
        Assert.Equal(observedAt, snapshot.LastWatchedAt);
    }

    private static PlanReminder Absolute(string id, int animeId, DateTimeOffset scheduled)
        => new(id, animeId, PlanReminderKind.Absolute, null, null, scheduled, scheduled,
            PlanReminderState.Pending, null, null);
}
