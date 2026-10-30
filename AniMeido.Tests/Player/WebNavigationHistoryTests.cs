using AniMeido.Plugin.Player.Sources.Web;

namespace AniMeido.Tests;

public sealed class WebNavigationHistoryTests
{
    [Fact]
    public void StartingBeforeObservationCannotCompleteItsNewRead()
    {
        var history = new WebNavigationHistory();
        history.RecordStarting(10);
        var results = new List<(bool Success, string Error)>();
        history.BeginObservation((success, error) => results.Add((success, error)));

        history.Complete(10, true, "old blank completion");

        Assert.Empty(results);
    }

    [Fact]
    public void CurrentObservationReceivesSuccessAndOriginalFailureDetails()
    {
        var history = new WebNavigationHistory();
        var results = new List<(bool Success, string Error)>();
        history.BeginObservation((success, error) => results.Add((success, error)));
        history.RecordStarting(11);
        history.RecordStarting(12);

        history.Complete(11, true, "Unknown");
        history.Complete(12, false, "ConnectionAborted");

        Assert.Equal(new[] { (true, "Unknown"), (false, "ConnectionAborted") }, results);
    }

    [Fact]
    public void UnknownCompletionIsAcceptedWithoutInventingAnOldOwner()
    {
        var history = new WebNavigationHistory();
        var results = new List<(bool Success, string Error)>();
        history.BeginObservation((success, error) => results.Add((success, error)));

        history.Complete(99, false, "Unknown navigation");

        Assert.Equal((false, "Unknown navigation"), Assert.Single(results));
    }

    [Fact]
    public void StartingFirstObservedDuringTheCurrentReadIsAcceptedWithoutGuessingItsOrigin()
    {
        var history = new WebNavigationHistory();
        var old = history.BeginObservation((_, _) => Assert.Fail("The old observation has ended."));
        history.EndObservation(old);
        var results = new List<(bool Success, string Error)>();
        history.BeginObservation((success, error) => results.Add((success, error)));

        history.RecordStarting(14);
        history.Complete(14, true, "First observed in current read");

        Assert.Equal((true, "First observed in current read"), Assert.Single(results));
    }

    [Fact]
    public void RepeatedStartingDoesNotReplaceTheFirstObservationOwner()
    {
        var history = new WebNavigationHistory();
        var old = history.BeginObservation((_, _) => Assert.Fail("The old observation has ended."));
        history.RecordStarting(15);
        history.EndObservation(old);
        var results = new List<(bool Success, string Error)>();
        history.BeginObservation((success, error) => results.Add((success, error)));
        history.RecordStarting(15);

        history.Complete(15, true, "Redirect completion");

        Assert.Empty(results);
    }

    [Fact]
    public void EndingAnOldObservationCannotDetachTheNewCallback()
    {
        var history = new WebNavigationHistory();
        var old = history.BeginObservation((_, _) => Assert.Fail("The old observation was replaced."));
        var results = new List<(bool Success, string Error)>();
        var current = history.BeginObservation((success, error) => results.Add((success, error)));
        Assert.NotEqual(old, current);

        history.EndObservation(old);
        history.Complete(20, true, "Current read");

        Assert.Equal((true, "Current read"), Assert.Single(results));
        history.EndObservation(current);
        history.Complete(21, true, "After end");
        Assert.Single(results);
    }

    [Fact]
    public void CompletionRemovesItsKnownHistoryEvenWhenItWasRejected()
    {
        var history = new WebNavigationHistory();
        history.RecordStarting(25);
        var results = new List<(bool Success, string Error)>();
        history.BeginObservation((success, error) => results.Add((success, error)));

        history.Complete(25, true, "Known old completion");
        Assert.Empty(results);
        history.Complete(25, true, "Now unknown");

        Assert.Equal((true, "Now unknown"), Assert.Single(results));
    }

    [Fact]
    public void ClearDetachesTheCallbackAndDropsPastControllerHistory()
    {
        var history = new WebNavigationHistory();
        history.RecordStarting(30);
        history.BeginObservation((_, _) => Assert.Fail("Closing the controller must detach its observer."));

        history.Clear();
        history.Complete(31, true, "No active observer");
        var results = new List<(bool Success, string Error)>();
        history.BeginObservation((success, error) => results.Add((success, error)));
        history.Complete(30, true, "Unknown in new controller");

        Assert.Equal((true, "Unknown in new controller"), Assert.Single(results));
    }
}
