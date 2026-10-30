using System.Collections.Concurrent;
using AniMeido.Plugin.Base.Services;
using AniMeido.Plugin.Base.ViewModels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace AniMeido.Tests;

public partial class RecommendationPreferenceViewModelTests
{
    private static readonly TimeSpan ActionBudget = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task RunAction_SerialBusyRequestDoesNotRunItsCallback()
    {
        var source = new NoNetworkSource();
        await using var candidates = new RecommendationCandidateProvider(source, NullLogger<RecommendationCandidateProvider>.Instance);
        await using var service = CreateService(candidates);
        var logger = new RecommendationActionLog();
        var vm = new RecommendationViewModel(service, CreateState(), logger);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var running = vm.RunActionAsync(async _ =>
        {
            entered.TrySetResult();
            await release.Task;
        }, CancellationToken.None);
        try
        {
            await entered.Task.WaitAsync(ActionBudget);
            var secondRan = false;
            await vm.RunActionAsync(_ =>
            {
                secondRan = true;
                return Task.CompletedTask;
            }, CancellationToken.None).WaitAsync(ActionBudget);

            Assert.False(secondRan);
            Assert.False(running.IsCompleted);
            Assert.Equal("正在处理上一项操作，请稍后重试。", vm.Message);
            Assert.False(vm.HasError);
            Assert.Empty(logger.Entries);
            Assert.Equal(0, source.Calls);
        }
        finally
        {
            release.TrySetResult();
            await running.WaitAsync(ActionBudget);
        }
    }

    [Fact]
    public async Task RunAction_NonSerialActionRunsConcurrentlyWithoutReleasingSerialAdmission()
    {
        var source = new NoNetworkSource();
        await using var candidates = new RecommendationCandidateProvider(source, NullLogger<RecommendationCandidateProvider>.Instance);
        await using var service = CreateService(candidates);
        var vm = new RecommendationViewModel(service, CreateState(), NullLogger.Instance);
        var serialEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var serialRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var parallelEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var parallelRelease = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var serial = vm.RunActionAsync(async _ =>
        {
            serialEntered.TrySetResult();
            await serialRelease.Task;
        }, CancellationToken.None);
        Task parallel = Task.CompletedTask;
        try
        {
            await serialEntered.Task.WaitAsync(ActionBudget);
            parallel = vm.RunActionAsync(async _ =>
            {
                parallelEntered.TrySetResult();
                await parallelRelease.Task;
            }, CancellationToken.None, serialize: false);
            await parallelEntered.Task.WaitAsync(ActionBudget);
            Assert.False(serial.IsCompleted);
            Assert.False(parallel.IsCompleted);
            parallelRelease.SetResult();
            await parallel.WaitAsync(ActionBudget);

            var nextSerialRan = false;
            await vm.RunActionAsync(_ =>
            {
                nextSerialRan = true;
                return Task.CompletedTask;
            }, CancellationToken.None).WaitAsync(ActionBudget);

            Assert.False(nextSerialRan);
            Assert.False(serial.IsCompleted);
            Assert.Equal("正在处理上一项操作，请稍后重试。", vm.Message);
            Assert.False(vm.HasError);
            Assert.Equal(0, source.Calls);
        }
        finally
        {
            parallelRelease.TrySetResult();
            serialRelease.TrySetResult();
            await Task.WhenAll(serial, parallel).WaitAsync(ActionBudget);
        }
    }

    [Fact]
    public async Task RunAction_CompletionReleasesAdmissionForTheNextSerialAction()
    {
        var source = new NoNetworkSource();
        await using var candidates = new RecommendationCandidateProvider(source, NullLogger<RecommendationCandidateProvider>.Instance);
        await using var service = CreateService(candidates);
        var vm = new RecommendationViewModel(service, CreateState(), NullLogger.Instance);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var completed = 0;
        var running = vm.RunActionAsync(async _ =>
        {
            entered.TrySetResult();
            await release.Task;
            completed++;
        }, CancellationToken.None);
        try
        {
            await entered.Task.WaitAsync(ActionBudget);
            release.SetResult();
            await running.WaitAsync(ActionBudget);
            await vm.RunActionAsync(_ =>
            {
                completed++;
                return Task.CompletedTask;
            }, CancellationToken.None).WaitAsync(ActionBudget);

            Assert.Equal(2, completed);
            Assert.Null(vm.Message);
            Assert.False(vm.HasError);
            Assert.Equal(0, source.Calls);
        }
        finally
        {
            release.TrySetResult();
            await running.WaitAsync(ActionBudget);
        }
    }

    [Fact]
    public async Task RunAction_CurrentFailureReportsOriginalErrorOnceAndReleasesAdmission()
    {
        var source = new NoNetworkSource();
        await using var candidates = new RecommendationCandidateProvider(source, NullLogger<RecommendationCandidateProvider>.Instance);
        await using var service = CreateService(candidates);
        var logger = new RecommendationActionLog();
        var vm = new RecommendationViewModel(service, CreateState(), logger);
        var failure = new HttpRequestException("fixture recommendation action failure");

        await vm.RunActionAsync(_ => Task.FromException(failure), CancellationToken.None).WaitAsync(ActionBudget);

        Assert.True(vm.HasError);
        Assert.Equal(failure.Message, vm.Message);
        var entry = Assert.Single(logger.Entries);
        Assert.Equal(LogLevel.Error, entry.Level);
        Assert.Same(failure, entry.Exception);
        var nextRan = false;
        await vm.RunActionAsync(_ =>
        {
            nextRan = true;
            return Task.CompletedTask;
        }, CancellationToken.None).WaitAsync(ActionBudget);
        Assert.True(nextRan);
        Assert.Single(logger.Entries);
        Assert.Equal(0, source.Calls);
    }

    [Fact]
    public async Task RunAction_CancelledFailureIsSilentAndReleasesAdmission()
    {
        var source = new NoNetworkSource();
        await using var candidates = new RecommendationCandidateProvider(source, NullLogger<RecommendationCandidateProvider>.Instance);
        await using var service = CreateService(candidates);
        var logger = new RecommendationActionLog();
        var vm = new RecommendationViewModel(service, CreateState(), logger);
        using var cancellation = new CancellationTokenSource();
        var actionRan = false;

        await vm.RunActionAsync(_ =>
        {
            actionRan = true;
            cancellation.Cancel();
            return Task.FromCanceled(cancellation.Token);
        }, cancellation.Token).WaitAsync(ActionBudget);

        Assert.True(actionRan);
        Assert.Null(vm.Message);
        Assert.False(vm.HasError);
        Assert.Empty(logger.Entries);
        var nextRan = false;
        await vm.RunActionAsync(_ =>
        {
            nextRan = true;
            return Task.CompletedTask;
        }, CancellationToken.None).WaitAsync(ActionBudget);
        Assert.True(nextRan);
        Assert.Equal(0, source.Calls);
    }

    [Fact]
    public async Task RunAction_PreCancelledRequestDoesNotRunOrChangeExistingMessage()
    {
        var source = new NoNetworkSource();
        await using var candidates = new RecommendationCandidateProvider(source, NullLogger<RecommendationCandidateProvider>.Instance);
        await using var service = CreateService(candidates);
        var logger = new RecommendationActionLog();
        var vm = new RecommendationViewModel(service, CreateState(), logger);
        vm.ReportError("保留当前操作的错误提示。");
        var ran = false;

        await vm.RunActionAsync(_ =>
        {
            ran = true;
            vm.ReportBusyAction();
            return Task.CompletedTask;
        }, new CancellationToken(true)).WaitAsync(ActionBudget);

        Assert.False(ran);
        Assert.Equal("保留当前操作的错误提示。", vm.Message);
        Assert.True(vm.HasError);
        Assert.Empty(logger.Entries);
        Assert.Equal(0, source.Calls);
    }

    [Fact]
    public async Task RunAction_CancelledPreviousVisitDoesNotReportBusyOrReleaseAcceptedSerialAction()
    {
        var source = new NoNetworkSource();
        await using var candidates = new RecommendationCandidateProvider(source, NullLogger<RecommendationCandidateProvider>.Instance);
        await using var service = CreateService(candidates);
        var logger = new RecommendationActionLog();
        var vm = new RecommendationViewModel(service, CreateState(), logger);
        using var previousVisit = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var running = vm.RunActionAsync(async _ =>
        {
            entered.TrySetResult();
            await release.Task;
        }, previousVisit.Token);
        try
        {
            await entered.Task.WaitAsync(ActionBudget);
            vm.Suspend();
            previousVisit.Cancel();
            vm.ReportError("保留新访问中的错误提示。");
            var oldRequestRan = false;

            await vm.RunActionAsync(_ =>
            {
                oldRequestRan = true;
                return Task.CompletedTask;
            }, previousVisit.Token).WaitAsync(ActionBudget);

            Assert.False(oldRequestRan);
            Assert.Equal("保留新访问中的错误提示。", vm.Message);
            Assert.True(vm.HasError);
            Assert.False(running.IsCompleted);
            Assert.Empty(logger.Entries);
            var currentRequestRan = false;
            await vm.RunActionAsync(_ =>
            {
                currentRequestRan = true;
                return Task.CompletedTask;
            }, CancellationToken.None).WaitAsync(ActionBudget);
            Assert.False(currentRequestRan);
            Assert.Equal("正在处理上一项操作，请稍后重试。", vm.Message);

            release.SetResult();
            await running.WaitAsync(ActionBudget);
            await vm.RunActionAsync(_ =>
            {
                currentRequestRan = true;
                return Task.CompletedTask;
            }, CancellationToken.None).WaitAsync(ActionBudget);
            Assert.True(currentRequestRan);
            Assert.Equal(0, source.Calls);
        }
        finally
        {
            release.TrySetResult();
            await running.WaitAsync(ActionBudget);
        }
    }

    private sealed class RecommendationActionLog : ILogger
    {
        public ConcurrentQueue<(LogLevel Level, Exception? Exception)> Entries { get; } = new();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => Entries.Enqueue((logLevel, exception));
    }
}
