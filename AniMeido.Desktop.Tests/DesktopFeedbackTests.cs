using AniMeido.Plugin.Base.Services;
using System.Text.RegularExpressions;
using Xunit;

namespace AniMeido.Desktop.Tests;

public sealed class DesktopFeedbackTests
{
    [Theory]
    [Trait("Category", "Mechanism")]
    [InlineData("CurrentSeasonPage.xaml.cs", "ToggleTimeMachineStatusSafelyAsync")]
    [InlineData("CurrentSeasonPage.xaml.cs", "ToggleStatusSafelyAsync")]
    [InlineData("AnimeDetailPage.xaml.cs", "OnPersonalArchiveClick")]
    [InlineData("AnimeDetailPage.xaml.cs", "CreateTagButton")]
    public void OperationFailuresOfferDesktopFeedbackAndKeepTheExistingFallback(string file, string method)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "AniMeido.slnx")))
            directory = directory.Parent;
        Assert.NotNull(directory);
        var source = File.ReadAllText(Path.Combine(directory.FullName, "Plugins",
            "AniMeido.Plugin.Base", "Views", file));
        var declaration = Regex.Match(source, $@"(?m)^\s*private [^\r\n]+\b{method}\s*\(");
        Assert.True(declaration.Success);
        var remainder = source[(declaration.Index + declaration.Length)..];
        var nextMethod = Regex.Match(remainder, @"(?m)^ {4,8}private ");
        var body = nextMethod.Success ? remainder[..nextMethod.Index] : remainder;

        Assert.Contains("#if UNO_DESKTOP", body);
        Assert.Contains("DesktopFeedback.TryShow", body);
        Assert.Contains("error: true", body);
        Assert.Contains("ErrorInfoBar.IsOpen = true", body);
    }

    [Fact]
    public void NoSubscriberKeepsThePageFallbackAvailable()
    {
        var fallbackShown = false;

        if (!DesktopFeedback.TryShow("已保存。"))
            fallbackShown = true;

        Assert.True(fallbackShown);
    }

    [Fact]
    public void SubscriberReceivesTheMessageBeforeTheCallReturns()
    {
        string? received = null;
        void Receive(string message, bool error) => received = message;
        DesktopFeedback.Requested += Receive;
        try
        {
            Assert.True(DesktopFeedback.TryShow("标签已保存。"));
            Assert.Equal("标签已保存。", received);
        }
        finally
        {
            DesktopFeedback.Requested -= Receive;
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ErrorClassificationReachesThePresenterUnchanged(bool isError)
    {
        bool? received = null;
        void Receive(string message, bool error) => received = error;
        DesktopFeedback.Requested += Receive;
        try
        {
            Assert.True(DesktopFeedback.TryShow("这次操作的结果。", isError));
            Assert.Equal(isError, received);
        }
        finally
        {
            DesktopFeedback.Requested -= Receive;
        }
    }

    [Fact]
    public void UnsubscribingRestoresFallbackAndStopsDelivery()
    {
        var calls = 0;
        void Receive(string message, bool error) => ++calls;
        DesktopFeedback.Requested += Receive;
        try
        {
            Assert.True(DesktopFeedback.TryShow("第一次。"));
        }
        finally
        {
            DesktopFeedback.Requested -= Receive;
        }

        Assert.False(DesktopFeedback.TryShow("第二次。", error: true));
        Assert.Equal(1, calls);
    }
}
