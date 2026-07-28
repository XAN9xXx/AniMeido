using AniMeido.Plugin.Player.Diagnostics;
using System.IO.Compression;

namespace AniMeido.Tests;

public sealed class PlaybackDiagnosticRecorderTests
{
    [Fact]
    public async Task Recorder_RedactsSecretsAndExportsZip()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"animeido-diagnostics-{Guid.NewGuid():N}");
        var exportPath = Path.Combine(
            Path.GetTempPath(),
            $"animeido-diagnostics-{Guid.NewGuid():N}.zip");
        try
        {
            await using var recorder =
                new PlaybackDiagnosticRecorder(root);
            await recorder.StartAsync(CancellationToken.None);
            recorder.Record(
                "http",
                "response",
                "source-a",
                new Uri(
                    "https://media.test/video.m3u8?token=uri-secret"),
                new Dictionary<string, object?>
                {
                    ["Cookie"] = "session=data-secret",
                    ["status"] = 200,
                    ["redirect"] =
                        "https://media.test/next?signature=field-secret",
                },
                """
                <input name="csrf" value="body-secret">
                https://media.test/play?token=body-url-secret
                """);
            await recorder.ExportAsync(
                exportPath,
                CancellationToken.None);

            Assert.True(File.Exists(exportPath));
            using var archive = ZipFile.OpenRead(exportPath);
            var eventsEntry = Assert.Single(
                archive.Entries,
                entry => entry.FullName == "events.jsonl");
            using var reader = new StreamReader(eventsEntry.Open());
            var content = await reader.ReadToEndAsync();

            Assert.DoesNotContain("uri-secret", content);
            Assert.DoesNotContain("data-secret", content);
            Assert.DoesNotContain("field-secret", content);
            Assert.DoesNotContain("body-secret", content);
            Assert.DoesNotContain("body-url-secret", content);
            Assert.Contains("redacted", content);
            Assert.Contains("\"status\":200", content);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }

            if (File.Exists(exportPath))
            {
                File.Delete(exportPath);
            }
        }
    }

    [Fact]
    public void SanitizeResponseSnippet_TruncatesLargeBodies()
    {
        var sanitized =
            PlaybackDiagnosticRecorder.SanitizeResponseSnippet(
                new string(
                    'x',
                    PlaybackDiagnosticRecorder.MaximumSnippetLength + 10));

        Assert.Equal(
            PlaybackDiagnosticRecorder.MaximumSnippetLength,
            sanitized.Length);
    }
}
