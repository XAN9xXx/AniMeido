using AniMeido.Plugin.Base.Services;
using Microsoft.Data.Sqlite;
using System.Diagnostics;
using System.Globalization;

namespace AniMeido.Tests;

public sealed partial class ArchiveStatisticsBaselineTests
{
    [Theory]
    [InlineData(1000, 1)]
    [InlineData(1000, 50)]
    [InlineData(10000, 1)]
    [InlineData(10000, 50)]
    [Trait("Category", "Performance")]
    public async Task EntryTimeIndex_PreservesStatisticsAndReportsReadWriteCosts(int count, int annualPercent)
    {
        var annualCount = count * annualPercent / 100;
        await SeedAsync(count, annualCount);
        var archive = new ArchiveService(Factory);
        output.WriteLine("Entry-time candidate only; rows per table={0}; annual rows={1} ({2}%).",
            count, annualCount, annualPercent);
        output.WriteLine("Production SQL, including nullable-year predicates, is unchanged."
            + " Plans below cover representative subqueries, not the full summary SQL.");

        await MeasureEntryTimeWritesAsync("before index");
        await MeasureEntryIndexScopesAsync(archive, count, annualCount, "before index");

        using (var connection = await Factory.OpenAsync())
        {
            var beforeBytes = await ReadActivePageBytesAsync(connection);
            using var command = connection.CreateCommand();
            command.CommandText = "CREATE INDEX candidate_archive_entry_time ON archive_entries(OccurredAt)";
            var started = Stopwatch.GetTimestamp();
            await command.ExecuteNonQueryAsync();
            var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            var afterBytes = await ReadActivePageBytesAsync(connection);
            output.WriteLine("Candidate creation: {0:F3} ms; active-page bytes before={1}, after={2}, delta={3}.",
                elapsed, beforeBytes, afterBytes, afterBytes - beforeBytes);
        }

        await MeasureEntryTimeWritesAsync("after index");
        await MeasureEntryIndexScopesAsync(archive, count, annualCount, "after index");
        await AssertSourceCountsAsync(count);
        output.WriteLine("No index-use or speedup assertion; unchanged results are required even without a gain."
            + " Run this class alone for process allocation measurements; this is not UI acceptance.");
    }

    private async Task MeasureEntryIndexScopesAsync(ArchiveService archive, int count, int annualCount, string phase)
    {
        output.WriteLine("Statistics phase={0}; 3 warmups / 5 samples per scope.", phase);
        await MeasureScopeAsync(archive, ReviewYear, Expected(annualCount, annualCount / 2 + 1));
        await MeasureScopeAsync(archive, EmptyYear, Expected(0, 0));
        await MeasureScopeAsync(archive, null, Expected(count, count / 2));
    }

    private async Task MeasureEntryTimeWritesAsync(string phase)
    {
        const int UpdateCount = 1000;
        var ids = Enumerable.Range(1, UpdateCount)
            .Select(id => "entry-" + id.ToString(CultureInfo.InvariantCulture))
            .ToArray();
        var timestamp = LocalYearStart(ReviewYear).AddMonths(6).ToString("O");
        var samples = new List<double>();
        using var connection = await Factory.OpenAsync();
        for (var iteration = 0; iteration < 8; iteration++)
        {
            using var transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            // 更新候选索引列；Body 和其他记录字段不参与写入，每轮随后回滚。
            command.CommandText = "UPDATE archive_entries SET OccurredAt=@at WHERE EntryId=@id";
            command.Parameters.AddWithValue("@at", timestamp);
            var id = command.Parameters.AddWithValue("@id", ids[0]);
            command.Prepare();

            var changed = 0;
            var started = Stopwatch.GetTimestamp();
            foreach (var entryId in ids)
            {
                id.Value = entryId;
                changed += await command.ExecuteNonQueryAsync();
            }
            var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;

            transaction.Rollback();
            Assert.Equal(UpdateCount, changed);
            if (iteration >= 3)
            {
                samples.Add(elapsed);
            }
        }

        output.WriteLine("Write phase={0}; {1} OccurredAt updates, each sample rolled back;"
            + " 3 warmups / 5 samples: median {2:F3} ms; samples ms=[{3}].",
            phase, UpdateCount, samples.Order().ElementAt(2),
            string.Join(", ", samples.Select(value => value.ToString("F3", CultureInfo.InvariantCulture))));
    }

    private static async Task<long> ReadActivePageBytesAsync(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT (page_count - freelist_count) * page_size
            FROM pragma_page_count(), pragma_freelist_count(), pragma_page_size()
            """;
        return Convert.ToInt64(await command.ExecuteScalarAsync(), CultureInfo.InvariantCulture);
    }
}
