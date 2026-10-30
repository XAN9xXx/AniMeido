using AniMeido.Contracts.Models;
using AniMeido.Contracts.Notifications;
using AniMeido.Plugin.Base.Services;
using AniMeido.Plugin.Base.ViewModels;
using Microsoft.Extensions.Logging.Abstractions;

namespace AniMeido.Tests;

public sealed partial class ManagementViewModelTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompletedStatusWriteAfterLeavingKeepsOldPresentationUntilTheNextLoad(bool remove)
    {
        var notifications = new WriteNotifications();
        var (vm, tracking) = await CreateWriteViewModelAsync(notifications);
        var original = Assert.Single(vm.Entries);
        var counts = vm.StatusSections.Select(section => (section.Status, section.Count)).ToArray();
        var summary = vm.SummaryText;
        AnimeTrackingStatus? target = remove ? null : AnimeTrackingStatus.Following;
        var writing = vm.ChangeStatusAsync([original], target);
        try
        {
            await notifications.Called.WaitAsync(Budget);
            Assert.Equal(target, await tracking.GetStatusAsync(1).WaitAsync(Budget));
            vm.CancelPendingLoads();
            vm.ErrorMessage = "Previous visit message";
            vm.IsError = true;

            notifications.Release();
            var result = await writing.WaitAsync(Budget);

            Assert.Equal(new StatusChangeResult(1, 0), result);
            Assert.Same(original, Assert.Single(vm.Entries));
            Assert.Equal(counts, vm.StatusSections.Select(section => (section.Status, section.Count)));
            Assert.Equal(summary, vm.SummaryText);
            Assert.True(vm.IsError);
            Assert.Equal("Previous visit message", vm.ErrorMessage);
            Assert.False(vm.IsLoading);
            Assert.False(vm.IsTagLoading);

            await vm.LoadAsync().WaitAsync(Budget);
            Assert.Empty(vm.Entries);
            Assert.False(vm.IsError);
            Assert.Null(vm.ErrorMessage);
            await vm.SelectSectionAsync(vm.StatusSections.Single(
                section => section.Status == AnimeTrackingStatus.Following)).WaitAsync(Budget);
            Assert.Equal(remove ? 0 : 1, vm.Entries.Count);
        }
        finally
        {
            notifications.Release();
            try
            {
                await writing.WaitAsync(Budget);
            }
            finally
            {
                vm.CancelPendingLoads();
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OldStatusWriteCannotRefreshOrClearTheReplacementLoad(bool remove)
    {
        var notifications = new WriteNotifications();
        var (vm, tracking) = await CreateWriteViewModelAsync(notifications);
        AnimeTrackingStatus? target = remove ? null : AnimeTrackingStatus.Following;
        var writing = vm.ChangeStatusAsync([Assert.Single(vm.Entries)], target);
        try
        {
            await notifications.Called.WaitAsync(Budget);
            Assert.Equal(target, await tracking.GetStatusAsync(1).WaitAsync(Budget));
            vm.CancelPendingLoads();
            await vm.LoadAsync().WaitAsync(Budget);
            var entries = vm.Entries.ToArray();
            var counts = vm.StatusSections.Select(section => (section.Status, section.Count)).ToArray();
            var header = vm.HeaderText;
            vm.ErrorMessage = "Replacement visit message";
            vm.IsError = true;

            notifications.Release();
            var result = await writing.WaitAsync(Budget);

            Assert.Equal(new StatusChangeResult(1, 0), result);
            Assert.Equal(entries, vm.Entries);
            Assert.Equal(counts, vm.StatusSections.Select(section => (section.Status, section.Count)));
            Assert.Equal(header, vm.HeaderText);
            Assert.True(vm.IsError);
            Assert.Equal("Replacement visit message", vm.ErrorMessage);
            Assert.False(vm.IsLoading);
            Assert.False(vm.IsTagLoading);
        }
        finally
        {
            notifications.Release();
            try
            {
                await writing.WaitAsync(Budget);
            }
            finally
            {
                vm.CancelPendingLoads();
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StatusWriteStillRefreshesTheSelectedSectionWithinTheSameLoad(bool remove)
    {
        var notifications = new WriteNotifications();
        var (vm, tracking) = await CreateWriteViewModelAsync(notifications);
        AnimeTrackingStatus? target = remove ? null : AnimeTrackingStatus.Following;
        var writing = vm.ChangeStatusAsync([Assert.Single(vm.Entries)], target);
        var following = vm.StatusSections.Single(section => section.Status == AnimeTrackingStatus.Following);
        try
        {
            await notifications.Called.WaitAsync(Budget);
            Assert.Equal(target, await tracking.GetStatusAsync(1).WaitAsync(Budget));
            await vm.SelectSectionAsync(following).WaitAsync(Budget);
            Assert.Empty(vm.Entries);

            notifications.Release();
            var result = await writing.WaitAsync(Budget);

            Assert.Equal(new StatusChangeResult(1, 0), result);
            Assert.Same(following, vm.SelectedSection);
            Assert.Equal(remove ? 0 : 1, vm.Entries.Count);
            if (!remove)
            {
                Assert.Equal(AnimeTrackingStatus.Following, Assert.Single(vm.Entries).Status);
            }
            Assert.Equal(0, vm.StatusSections.Single(section => section.Status == AnimeTrackingStatus.Watching).Count);
            Assert.Equal(remove ? 0 : 1, following.Count);
            Assert.False(vm.IsError);
        }
        finally
        {
            notifications.Release();
            try
            {
                await writing.WaitAsync(Budget);
            }
            finally
            {
                vm.CancelPendingLoads();
            }
        }
    }

    private async Task<(ManagementViewModel ViewModel, TrackingService Tracking)> CreateWriteViewModelAsync(
        WriteNotifications notifications)
    {
        await RunProductionMigrationAsync();
        // 初始状态不用通知门闩；只有被测写入在真实提交后暂停。
        await new TrackingService(DbFactory).SetStatusAsync(1, AnimeTrackingStatus.Watching).WaitAsync(Budget);
        var tracking = new TrackingService(DbFactory, notifications);
        var source = new ManagementSource();
        var vm = new ManagementViewModel(tracking, source, new SavedTagService(DbFactory),
            new LocalSearchService(tracking, source, NullLogger<LocalSearchService>.Instance),
            new ArchiveService(DbFactory), NullLogger.Instance);
        await vm.LoadAsync().WaitAsync(Budget);
        await vm.SelectSectionAsync(vm.StatusSections.Single(
            section => section.Status == AnimeTrackingStatus.Watching)).WaitAsync(Budget);
        Assert.Equal(1, Assert.Single(vm.Entries).Anime.ID);
        return (vm, tracking);
    }

    private sealed class WriteNotifications : IAppNotificationService
    {
        private readonly TaskCompletionSource _called = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal Task Called => _called.Task;
        internal void Release() => _release.TrySetResult();
        public bool IsSupported => true;
        public bool NotificationsEnabled => true;
        public Task CancelGroupAsync(string group, CancellationToken cancellationToken = default)
        {
            Assert.Equal("anime-plan-1", group);
            _called.TrySetResult();
            return _release.Task;
        }

        public Task ScheduleAsync(AppNotificationRequest request, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
        public Task CancelAsync(string group, string tag, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
        public Task OpenNotificationSettingsAsync() => Task.CompletedTask;
        public IDisposable RegisterActivationHandler(string category, Func<AppNotificationActivation, CancellationToken, Task> handler)
            => new Registration();
        private sealed class Registration : IDisposable
        {
            public void Dispose() { }
        }
    }
}
