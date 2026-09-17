using AniMeido.Plugin.Base.Services;
using AniMeido.Plugin.Base.ViewModels;

namespace AniMeido.Tests;

/// <summary>
/// 智能列表页每次加载都会再同步一次播放器可用性。候选字段集合一旦被替换，
/// 绑定到它的下拉框会清空选中项，曾在把空值写回枚举字段时抛出空引用异常。
/// </summary>
public sealed class SmartListsViewModelTests : DbTestBase
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SyncingUnchangedAvailability_KeepsFieldCollection(bool isAvailable)
    {
        var vm = CreateViewModel(isAvailable);
        var fields = vm.Fields;

        vm.SetPlaybackAvailability(isAvailable);

        Assert.Same(fields, vm.Fields);
    }

    [Fact]
    public void ChangingAvailability_StillUpdatesPlaybackFields()
    {
        var vm = CreateViewModel(isAvailable: false);
        Assert.DoesNotContain(vm.Fields, SmartListEvaluator.IsPlaybackField);

        vm.SetPlaybackAvailability(true);
        Assert.Contains(vm.Fields, SmartListEvaluator.IsPlaybackField);

        vm.SetPlaybackAvailability(false);
        Assert.DoesNotContain(vm.Fields, SmartListEvaluator.IsPlaybackField);
    }

    private SmartListsViewModel CreateViewModel(bool isAvailable) => new(
        new ActionCenterService(DbFactory),
        new TrackingService(DbFactory),
        // 同步可用性不访问数据源。
        null!,
        isAvailable);
}
