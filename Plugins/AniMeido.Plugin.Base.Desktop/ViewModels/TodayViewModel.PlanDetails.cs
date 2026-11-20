using AniMeido.Contracts.Models;

namespace AniMeido.Plugin.Base.ViewModels;

public partial class TodayViewModel
{
    private readonly SemaphoreSlim _planDetailsQueue = new(1, 1);
    private PlanDetailsRequest? _planDetailsRequest;
    private bool _isPlanDeckExpanded;

    public Task SetPlanDeckExpandedAsync(bool expanded)
    {
        _isPlanDeckExpanded = expanded;
        if (expanded)
            return EnsurePlanDetailsAsync(_loadVersion, CancellationToken.None);

        // 前排仍是折叠画面所需内容；收起只取消已经开始的后排补图。
        if (_planDetailsRequest is { IsResolvingRear: true })
            CancelPlanDetailsRequest();
        return Task.CompletedTask;
    }

    public void SuspendPlanDetails()
    {
        _isPlanDeckExpanded = false;
        CancelPlanDetailsRequest();
    }

    private Task EnsurePlanDetailsAsync(int loadVersion, CancellationToken cancellationToken)
    {
        if (_planDetailsRequest is { } current
            && current.LoadVersion == loadVersion
            && !current.Pending.IsCompleted)
        {
            return current.Pending;
        }

        CancelPlanDetailsRequest();
        var request = new PlanDetailsRequest(
            loadVersion,
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken));
        _planDetailsRequest = request;
        _ = LoadPlanDetailsAsync(request);
        return request.Pending;
    }

    private async Task LoadPlanDetailsAsync(PlanDetailsRequest request)
    {
        var token = request.Cancellation.Token;
        var enteredQueue = false;
        try
        {
            // 来源可能忽略取消；旧批真正结束前不另起四条，避免快速开关叠加并发。
            await _planDetailsQueue.WaitAsync(token);
            enteredQueue = true;
            var attempted = new HashSet<int>();
            while (IsCurrentPlanDetails(request))
            {
                var front = Plans.Take(TodayPlanDeckPresentation.FoldedCardCount);
                var missing = MissingPlanDetails(front, attempted);
                request.IsResolvingRear = false;
                if (missing.Count == 0 && _isPlanDeckExpanded)
                {
                    missing = MissingPlanDetails(
                        Plans.Skip(TodayPlanDeckPresentation.FoldedCardCount), attempted);
                    request.IsResolvingRear = missing.Count > 0;
                }
                if (missing.Count == 0)
                    return;

                attempted.UnionWith(missing);
                var loaded = await ResolveAnimeAsync(
                    missing, _seasonal.ToDictionary(anime => anime.ID), request.LoadVersion, token);
                if (!IsCurrentPlanDetails(request))
                    return;

                PublishPlanDetails(loaded, missing);
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
        }
#pragma warning disable CA1031 // 请求任务通过 Completion 转交异常，入口调用方负责最终报告。
        catch (Exception ex)
        {
            request.Completion.TrySetException(ex);
        }
#pragma warning restore CA1031
        finally
        {
            if (ReferenceEquals(_planDetailsRequest, request))
                _planDetailsRequest = null;
            if (enteredQueue)
                _planDetailsQueue.Release();
            request.Cancellation.Dispose();
            request.Completion.TrySetResult();
        }
    }

    private async Task<IReadOnlyList<Anime>> ResolveMissingPlanTitlesAsync(
        IReadOnlyList<int> animeIds,
        IReadOnlyDictionary<int, Anime> seasonal,
        int loadVersion,
        CancellationToken cancellationToken)
    {
        if (animeIds.Count == 0)
            return [];

        // 旧记录补建也会请求计划详情，与尚未排空的封面请求共用实际并发边界。
        await _planDetailsQueue.WaitAsync(cancellationToken);
        try
        {
            return await ResolveAnimeAsync(animeIds, seasonal, loadVersion, cancellationToken);
        }
        finally
        {
            _planDetailsQueue.Release();
        }
    }

    private static List<int> MissingPlanDetails(
        IEnumerable<TodayPlanEntry> entries,
        IReadOnlySet<int> attempted)
        => entries
            .Where(entry => entry.Anime is null && !attempted.Contains(entry.Plan.AnimeId))
            .Select(entry => entry.Plan.AnimeId)
            .ToList();

    private void PublishPlanDetails(IReadOnlyList<Anime> loaded, IReadOnlyCollection<int> requested)
    {
        var requestedIds = requested.ToHashSet();
        var byId = new Dictionary<int, Anime>();
        foreach (var anime in loaded)
        {
            if (requestedIds.Contains(anime.ID))
                byId[anime.ID] = anime;
        }

        var changed = false;
        for (var index = 0; index < Plans.Count; index++)
        {
            var entry = Plans[index];
            if (byId.TryGetValue(entry.Plan.AnimeId, out var anime) && entry.Anime != anime)
            {
                Plans[index] = entry with { Anime = anime };
                changed = true;
            }
        }
        if (changed)
            UpdatePlanSummary();
    }

    private bool IsCurrentPlanDetails(PlanDetailsRequest request)
        => ReferenceEquals(_planDetailsRequest, request)
            && request.LoadVersion == _loadVersion
            && !request.Cancellation.IsCancellationRequested;

    private void CancelPlanDetailsRequest()
    {
        var request = _planDetailsRequest;
        _planDetailsRequest = null;
        request?.Cancellation.Cancel();
    }

    private sealed class PlanDetailsRequest(int loadVersion, CancellationTokenSource cancellation)
    {
        public int LoadVersion { get; } = loadVersion;
        public CancellationTokenSource Cancellation { get; } = cancellation;
        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task Pending => Completion.Task;
        public bool IsResolvingRear { get; set; }
    }
}
