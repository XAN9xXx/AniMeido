using AniMeido.Plugin.Base.Models;
using AniMeido.Plugin.Base.Services;
using AniMeido.Plugin.Base.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;

namespace AniMeido.Tests;

public partial class RecommendationPreferenceViewModelTests
{
    [Fact]
    public async Task RunAction_DeferredConfirmationKeepsOriginalVisitTokenAndCannotSaveAfterNavigation()
    {
        await RunProductionMigrationAsync();
        var source = new NoNetworkSource();
        await using var candidates = new RecommendationCandidateProvider(source, NullLogger<RecommendationCandidateProvider>.Instance);
        await using var service = CreateService(candidates);
        var feature = new RecommendationFeature(RecommendationFeatureKind.Tag, "旅行", "旅行");
        await service.SetFeaturePreferenceAsync(feature, RecommendationAdjustment.Like);
        var original = Assert.Single(await service.GetFeaturePreferencesAsync());
        var logger = new RecommendationActionLog();
        var vm = new RecommendationViewModel(service, CreateState(), logger);
        using var previousVisit = new CancellationTokenSource();
        using var currentVisit = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var originalToken = CancellationToken.None;
        var attemptedSave = false;
        var saved = false;
        var running = vm.RunActionAsync(async token =>
        {
            originalToken = token;
            entered.TrySetResult();
            await release.Task;
            attemptedSave = true;
            await service.SetFeaturePreferenceAsync(feature, RecommendationAdjustment.Reduce, token);
            saved = true;
        }, previousVisit.Token);
        try
        {
            await entered.Task.WaitAsync(ActionBudget);
            Assert.Equal(previousVisit.Token, originalToken);
            previousVisit.Cancel();
            vm.Suspend();
            var currentToken = CancellationToken.None;
            await vm.RunActionAsync(token =>
            {
                currentToken = token;
                return Task.CompletedTask;
            }, currentVisit.Token, serialize: false).WaitAsync(ActionBudget);
            Assert.Equal(currentVisit.Token, currentToken);
            Assert.False(currentToken.IsCancellationRequested);
            Assert.True(originalToken.IsCancellationRequested);

            release.SetResult();
            await running.WaitAsync(ActionBudget);

            Assert.True(attemptedSave);
            Assert.False(saved);
            Assert.Equal(previousVisit.Token, originalToken);
            Assert.True(originalToken.IsCancellationRequested);
            Assert.Equal(original, Assert.Single(await service.GetFeaturePreferencesAsync()));
            Assert.Null(vm.Message);
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
    public async Task RunAction_LatePreviousVisitFailureKeepsCurrentMessageAndReleasesSerialAdmission()
    {
        await RunProductionMigrationAsync();
        var source = new NoNetworkSource();
        await using var candidates = new RecommendationCandidateProvider(source, NullLogger<RecommendationCandidateProvider>.Instance);
        await using var service = CreateService(candidates);
        var logger = new RecommendationActionLog();
        var vm = new RecommendationViewModel(service, CreateState(), logger);
        using var previousVisit = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var failure = new HttpRequestException("fixture late previous visit failure");
        var running = vm.RunActionAsync(async _ =>
        {
            entered.TrySetResult();
            await release.Task;
            throw failure;
        }, previousVisit.Token);
        try
        {
            await entered.Task.WaitAsync(ActionBudget);
            previousVisit.Cancel();
            vm.Suspend();
            vm.ReportError("保留当前访问中的错误提示。");
            release.SetResult();
            await running.WaitAsync(ActionBudget);

            Assert.Equal("保留当前访问中的错误提示。", vm.Message);
            Assert.True(vm.HasError);
            Assert.Empty(logger.Entries);
            var nextRan = false;
            await vm.RunActionAsync(_ =>
            {
                nextRan = true;
                return Task.CompletedTask;
            }, CancellationToken.None).WaitAsync(ActionBudget);
            Assert.True(nextRan);
            Assert.Equal("保留当前访问中的错误提示。", vm.Message);
            Assert.Empty(logger.Entries);
            Assert.Equal(0, source.Calls);
        }
        finally
        {
            release.TrySetResult();
            await running.WaitAsync(ActionBudget);
        }
    }
}
