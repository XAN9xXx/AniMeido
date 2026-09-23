using AniMeido.App.Services;
using Serilog;

namespace AniMeido.Tests;

public sealed class DiagnosticFileSinkTests
{
    [Fact]
    public void Warnings_RotateAtFiftyKiB_WithoutDroppingLaterEntries()
    {
        var root = NewRoot();
        try
        {
            using var logger = NewLogger(root);
            for (var index = 0; index < 20; index++)
                logger.Warning("Warning {Index}: {Details}", index, new string('x', 4096));

            var day = DateTime.Now.ToString("yyyy-MM-dd");
            var files = Directory.GetFiles(Path.Combine(root, "warning", day));
            Assert.True(files.Length > 1);
            Assert.All(files, file => Assert.InRange(new FileInfo(file).Length, 1, 50 * 1024));
            Assert.Contains(files, file => File.ReadAllText(file).Contains("Warning 19"));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Errors_CreateSeparateFiles_AndOldDaysAreRemoved()
    {
        var root = NewRoot();
        try
        {
            var oldDay = DateTime.Now.AddDays(-3).ToString("yyyy-MM-dd");
            var oldDirectory = Path.Combine(root, "warning", oldDay);
            Directory.CreateDirectory(oldDirectory);
            File.WriteAllText(Path.Combine(oldDirectory, "warnings.log"), "old");

            using var logger = NewLogger(root);
            logger.Error(new InvalidOperationException("first"), "First operation failed");
            logger.Error(new InvalidOperationException("second"), "Second operation failed");

            var day = DateTime.Now.ToString("yyyy-MM-dd");
            var files = Directory.GetFiles(Path.Combine(root, "error", day));
            Assert.Equal(2, files.Length);
            Assert.All(files, file => Assert.Contains("InvalidOperationException", Path.GetFileName(file)));
            Assert.False(Directory.Exists(oldDirectory));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void UndeletableOldDay_DoesNotBlockWriting()
    {
        var root = NewRoot();
        try
        {
            var oldDay = DateTime.Now.AddDays(-3).ToString("yyyy-MM-dd");
            var oldDirectory = Path.Combine(root, "warning", oldDay);
            Directory.CreateDirectory(oldDirectory);
            var lockedPath = Path.Combine(oldDirectory, "warnings.log");
            File.WriteAllText(lockedPath, "old");

            // 旧日志被别的程序占用时删不掉；这不能让当天的日志跟着丢失。
            using (new FileStream(lockedPath, FileMode.Open, FileAccess.Read, FileShare.None))
            using (var logger = NewLogger(root))
            {
                logger.Warning("First warning");
                logger.Warning("Second warning");
            }

            var day = DateTime.Now.ToString("yyyy-MM-dd");
            var text = File.ReadAllText(Path.Combine(root, "warning", day, "warnings.log"));
            Assert.Contains("First warning", text);
            Assert.Contains("Second warning", text);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static Serilog.Core.Logger NewLogger(string root)
        => new LoggerConfiguration()
            .MinimumLevel.Warning()
            .WriteTo.Sink(new DiagnosticFileSink(root))
            .CreateLogger();

    private static string NewRoot()
    {
        var path = Path.Combine(Path.GetTempPath(), "AniMeido-LogTests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
