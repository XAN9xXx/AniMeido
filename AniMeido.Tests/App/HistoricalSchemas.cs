namespace AniMeido.Tests;

/// <summary>从各版本历史 App 建表与迁移源码还原的完整 SQLite 夹具。</summary>
internal static class HistoricalSchemas
{
    private const string InitialTables = """
        CREATE TABLE IF NOT EXISTS tracking(AnimeID INTEGER PRIMARY KEY, Status INTEGER NOT NULL, UpdatedAt TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS cache(CacheKey TEXT PRIMARY KEY, Data TEXT NOT NULL, ExpiresAt TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS config(Key TEXT PRIMARY KEY, Value TEXT NOT NULL);
        CREATE TABLE IF NOT EXISTS browse_history(AnimeID INTEGER PRIMARY KEY, TitleSnapshot TEXT, LastViewedAt TEXT NOT NULL, ViewCount INTEGER NOT NULL DEFAULT 1);
        CREATE TABLE IF NOT EXISTS saved_tags(TagName TEXT NOT NULL PRIMARY KEY);
        """;

    // 2f0258ec75ae0c8eb7f1e7a75b394729915d1346: AnimeAssist.App/Services/DatabaseService.cs
    public const string V4 = InitialTables + """
        CREATE TABLE IF NOT EXISTS anime_plans(
            AnimeId INTEGER PRIMARY KEY,
            TitleSnapshot TEXT NOT NULL,
            Priority INTEGER NOT NULL DEFAULT 1,
            TargetStartDate TEXT NULL,
            SortOrder INTEGER NOT NULL DEFAULT 0,
            CreatedAt TEXT NOT NULL,
            UpdatedAt TEXT NOT NULL,
            StartedAt TEXT NULL,
            ArchivedAt TEXT NULL
        );

        CREATE TABLE IF NOT EXISTS plan_reminders(
            ReminderId TEXT PRIMARY KEY,
            AnimeId INTEGER NOT NULL,
            Kind INTEGER NOT NULL,
            RelativeDays INTEGER NULL,
            TimeOfDay TEXT NULL,
            AbsoluteAt TEXT NULL,
            ScheduledFor TEXT NOT NULL,
            State INTEGER NOT NULL DEFAULT 0,
            CatchUpSentAt TEXT NULL,
            HandledAt TEXT NULL,
            FOREIGN KEY(AnimeId)
                REFERENCES anime_plans(AnimeId)
                ON DELETE CASCADE
        );

        CREATE INDEX IF NOT EXISTS
            IX_plan_reminders_anime_state
            ON plan_reminders(AnimeId, State);
        CREATE INDEX IF NOT EXISTS
            IX_plan_reminders_schedule
            ON plan_reminders(State, ScheduledFor);

        CREATE TABLE IF NOT EXISTS anime_progress(
            AnimeId INTEGER PRIMARY KEY,
            CurrentEpisode INTEGER NOT NULL,
            PositionSeconds REAL NOT NULL,
            DurationSeconds REAL NOT NULL,
            LastWatchedAt TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS episode_progress(
            AnimeId INTEGER NOT NULL,
            EpisodeNumber INTEGER NOT NULL,
            PositionSeconds REAL NOT NULL,
            DurationSeconds REAL NOT NULL,
            IsCompleted INTEGER NOT NULL,
            LastWatchedAt TEXT NOT NULL,
            PRIMARY KEY(AnimeId, EpisodeNumber)
        );

        CREATE TABLE IF NOT EXISTS watch_sessions(
            EventId TEXT PRIMARY KEY,
            AnimeId INTEGER NOT NULL,
            EpisodeNumber INTEGER NOT NULL,
            PositionSeconds REAL NOT NULL,
            DurationSeconds REAL NOT NULL,
            IsCompleted INTEGER NOT NULL,
            ObservedAt TEXT NOT NULL
        );

        CREATE INDEX IF NOT EXISTS
            IX_watch_sessions_anime_observed
            ON watch_sessions(AnimeId, ObservedAt);

        CREATE TABLE IF NOT EXISTS smart_lists(
            Id TEXT PRIMARY KEY,
            Name TEXT NOT NULL,
            SchemaVersion INTEGER NOT NULL,
            RuleJson TEXT NOT NULL,
            SortJson TEXT NULL,
            CreatedAt TEXT NOT NULL,
            UpdatedAt TEXT NOT NULL
        );

        PRAGMA user_version = 4;
        """;

    // 04e177ac535f402b1913065245e3d5d27f7fd727: AnimeAssist.App/Services/DatabaseService.cs
    public const string V5 = V4 + """
        CREATE TABLE IF NOT EXISTS anime_archives(
            AnimeId INTEGER PRIMARY KEY,
            TitleSnapshot TEXT NOT NULL,
            PersonalRating REAL NULL CHECK(
                PersonalRating IS NULL OR
                (PersonalRating >= 0.5 AND
                 PersonalRating <= 10.0 AND
                 PersonalRating * 2 =
                    CAST(PersonalRating * 2 AS INTEGER))),
            SummaryNote TEXT NOT NULL DEFAULT '',
            CreatedAt TEXT NOT NULL,
            UpdatedAt TEXT NOT NULL
        );

        CREATE TABLE IF NOT EXISTS archive_entries(
            EntryId TEXT PRIMARY KEY,
            AnimeId INTEGER NOT NULL,
            OccurredAt TEXT NOT NULL,
            EpisodeNumber INTEGER NULL,
            Body TEXT NOT NULL,
            CreatedAt TEXT NOT NULL,
            UpdatedAt TEXT NOT NULL,
            FOREIGN KEY(AnimeId)
                REFERENCES anime_archives(AnimeId)
                ON DELETE CASCADE
        );
        CREATE INDEX IF NOT EXISTS
            IX_archive_entries_anime_time
            ON archive_entries(AnimeId, OccurredAt);

        CREATE TABLE IF NOT EXISTS personal_tags(
            TagId INTEGER PRIMARY KEY AUTOINCREMENT,
            Name TEXT NOT NULL COLLATE NOCASE UNIQUE
        );
        CREATE TABLE IF NOT EXISTS anime_personal_tags(
            AnimeId INTEGER NOT NULL,
            TagId INTEGER NOT NULL,
            PRIMARY KEY(AnimeId, TagId),
            FOREIGN KEY(AnimeId)
                REFERENCES anime_archives(AnimeId)
                ON DELETE CASCADE,
            FOREIGN KEY(TagId)
                REFERENCES personal_tags(TagId)
                ON DELETE CASCADE
        );

        CREATE TABLE IF NOT EXISTS screenshots(
            ScreenshotId TEXT PRIMARY KEY,
            FilePath TEXT NOT NULL,
            Sha256 TEXT NOT NULL,
            CapturedAt TEXT NOT NULL,
            WindowTitle TEXT NOT NULL,
            ProcessName TEXT NOT NULL,
            Width INTEGER NOT NULL,
            Height INTEGER NOT NULL,
            AnimeId INTEGER NULL,
            AnimeTitle TEXT NULL,
            EpisodeNumber INTEGER NULL,
            PlaybackPositionSeconds REAL NULL,
            ContextNote TEXT NOT NULL DEFAULT ''
        );
        CREATE INDEX IF NOT EXISTS
            IX_screenshots_anime_time
            ON screenshots(AnimeId, CapturedAt);

        CREATE TABLE IF NOT EXISTS screenshot_personal_tags(
            ScreenshotId TEXT NOT NULL,
            TagId INTEGER NOT NULL,
            PRIMARY KEY(ScreenshotId, TagId),
            FOREIGN KEY(ScreenshotId)
                REFERENCES screenshots(ScreenshotId)
                ON DELETE CASCADE,
            FOREIGN KEY(TagId)
                REFERENCES personal_tags(TagId)
                ON DELETE CASCADE
        );

        CREATE TABLE IF NOT EXISTS manual_watch_events(
            EventId TEXT PRIMARY KEY,
            AnimeId INTEGER NOT NULL,
            TitleSnapshot TEXT NOT NULL,
            OccurredAt TEXT NOT NULL,
            EpisodeFrom INTEGER NOT NULL,
            EpisodeTo INTEGER NOT NULL,
            DurationMinutes INTEGER NULL,
            Note TEXT NOT NULL DEFAULT '',
            CreatedAt TEXT NOT NULL
        );
        CREATE INDEX IF NOT EXISTS
            IX_manual_watch_events_anime_time
            ON manual_watch_events(AnimeId, OccurredAt);

        CREATE TABLE IF NOT EXISTS tracking_events(
            EventId TEXT PRIMARY KEY,
            AnimeId INTEGER NOT NULL,
            PreviousStatus INTEGER NULL,
            NewStatus INTEGER NOT NULL,
            ChangedAt TEXT NOT NULL
        );
        CREATE INDEX IF NOT EXISTS
            IX_tracking_events_anime_time
            ON tracking_events(AnimeId, ChangedAt);

        PRAGMA user_version = 5;
        """;

    // 2127571f7dc99c9c96861e29fcf4e034d87f77c7: AnimeAssist.App/Services/DatabaseService.cs
    public const string V6 = V5 + """
        CREATE TABLE IF NOT EXISTS
            recommendation_feature_preferences(
                FeatureKind INTEGER NOT NULL,
                FeatureKey TEXT NOT NULL COLLATE NOCASE,
                DisplayName TEXT NOT NULL,
                Adjustment INTEGER NOT NULL CHECK(
                    Adjustment IN (-1, 1)),
                UpdatedAt TEXT NOT NULL,
                PRIMARY KEY(FeatureKind, FeatureKey)
            );

        CREATE TABLE IF NOT EXISTS
            recommendation_hidden_anime(
                AnimeId INTEGER PRIMARY KEY,
                TitleSnapshot TEXT NOT NULL,
                HiddenAt TEXT NOT NULL
            );

        PRAGMA user_version = 6;
        """;
}
