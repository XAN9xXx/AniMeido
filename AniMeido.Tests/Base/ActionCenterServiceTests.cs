using AniMeido.Contracts.Playback;
using AniMeido.Plugin.Base.Models;
using AniMeido.Plugin.Base.Services;
using Microsoft.Data.Sqlite;

namespace AniMeido.Tests;

public sealed class ActionCenterServiceTests : DbTestBase
{
    [Fact]
    public async Task PlanAndReminderMappings_RoundTrip()
    {
        await RunProductionMigrationAsync();
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
        await RunProductionMigrationAsync();
        var service = new ActionCenterService(DbFactory);
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
    }

    [Fact]
    public async Task RecordAsync_DeduplicatesAndDoesNotRegressEpisode()
    {
        await RunProductionMigrationAsync();
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
    public async Task RecordAsync_NaturalEndCompletesEpisodeBelowNinetyPercent()
    {
        await RunProductionMigrationAsync();
        var service = new ActionCenterService(DbFactory);
        await service.RecordAsync(new AnimePlaybackProgress(
            "natural-end",
            200,
            1,
            400,
            600,
            true,
            DateTimeOffset.UtcNow));

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
        await RunProductionMigrationAsync();
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

    [Fact]
    public void SmartListEvaluator_AppliesNestedRulesAndSort()
    {
        var now = DateTimeOffset.UtcNow;
        var definition = new SmartListDefinition(
            "overdue-scifi",
            "逾期科幻",
            SmartListEvaluator.SchemaVersion,
            new SmartListRuleGroup(
                SmartListGroupMode.All,
                [
                    new SmartListCondition(
                        SmartListField.IsOverdue,
                        SmartListOperator.Equals,
                        "true"),
                ],
                [
                    new SmartListRuleGroup(
                        SmartListGroupMode.Any,
                        [
                            new SmartListCondition(
                                SmartListField.Tags,
                                SmartListOperator.ContainsAny,
                                "科幻,原创"),
                            new SmartListCondition(
                                SmartListField.Title,
                                SmartListOperator.Contains,
                                "机动"),
                        ]),
                ]),
            new SmartListSort(SmartListField.PlanPriority, true),
            now,
            now);
        var candidates = new[]
        {
            CreateCandidate(1, "低优先级", 0, true, ["科幻"]),
            CreateCandidate(2, "高优先级", 3, true, ["原创"]),
            CreateCandidate(3, "未逾期", 3, false, ["科幻"]),
        };

        var result = SmartListEvaluator.Apply(definition, candidates);

        Assert.Equal([2, 1], result.Select(item => item.AnimeId));
    }

    [Theory]
    [InlineData(SmartListField.CurrentEpisode, true)]
    [InlineData(SmartListField.HasIncompleteEpisode, true)]
    [InlineData(SmartListField.LastWatchedAt, true)]
    [InlineData(SmartListField.TrackingStatus, false)]
    [InlineData(SmartListField.PlanPriority, false)]
    public void SmartListEvaluator_ClassifiesPlaybackFields(
        SmartListField field,
        bool expected)
        => Assert.Equal(
            expected,
            SmartListEvaluator.IsPlaybackField(field));

    private static SmartListCandidate CreateCandidate(
        int animeId,
        string title,
        int priority,
        bool overdue,
        IReadOnlyList<string> tags)
        => new(
            animeId,
            title,
            null,
            0,
            false,
            priority,
            null,
            overdue,
            null,
            null,
            tags,
            null,
            null);
}
