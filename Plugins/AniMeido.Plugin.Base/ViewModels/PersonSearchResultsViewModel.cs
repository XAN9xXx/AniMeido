using System.Collections.ObjectModel;
using System.Text.Json;
using AniMeido.Contracts;
using AniMeido.Contracts.Models;
using AniMeido.Plugin.Base.Exceptions;
using AniMeido.Plugin.Base.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;

namespace AniMeido.Plugin.Base.ViewModels;

public sealed partial class PersonSearchResultsViewModel(
    IAnimeDataSource dataSource,
    TrackingService tracking,
    ILogger logger) : ObservableObject
{
    private const int DetailBatchSize = 4;
    private readonly ILogger _logger = logger;
    private readonly PanelLoadState _load = new();
    private int? _personId;
    private List<PersonWork>? _works;
    private int _processedCount;
    private HashSet<int> _blocked = [];

    public ObservableCollection<Anime> Results { get; } = [];

    [ObservableProperty]
    private string _title = "";

    [ObservableProperty]
    private string _resultText = "";

    [ObservableProperty]
    private bool _isLoading;

    public Task LoadAsync(int personId, string personName)
    {
        _load.Invalidate();
        _personId = personId;
        _works = null;
        _processedCount = 0;
        _blocked = [];
        Title = $"声优作品：{personName}";
        return EnsureLoadedAsync();
    }

    public void CancelLoad()
    {
        _load.CancelInflight();
        IsLoading = false;
    }

    public Task ResumeAsync()
        => _load.IsLoading ? EnsureLoadedAsync() : ResumeCoreAsync();

    private async Task ResumeCoreAsync()
    {
        var generation = _load.Generation;
        await RefreshBlockedOnlyAsync(generation);
        if (generation != _load.Generation || _personId is null || _load.IsLoaded)
            return;

        await EnsureLoadedAsync();
    }

    private async Task EnsureLoadedAsync()
    {
        var pending = _load.EnsureAsync(LoadCoreAsync, CancellationToken.None);
        var generation = _load.Generation;
        try
        {
            await pending;
        }
        catch (OperationCanceledException)
        {
        }
        catch (HttpRequestException ex)
        {
            if (generation == _load.Generation)
                ResultText = $"加载失败：{ex.Message}";
        }
        catch (MetadataServiceException ex)
        {
            if (generation == _load.Generation)
                ResultText = $"数据源请求失败：{ex.Message}";
        }
        catch (JsonException ex)
        {
            if (generation == _load.Generation)
                ResultText = $"数据解析失败：{ex.Message}";
        }
    }

    private async Task LoadCoreAsync(int generation, CancellationToken token)
    {
        if (_personId is not { } personId)
            return;

        IsLoading = _works is null;
        try
        {
            token.ThrowIfCancellationRequested();
            if (_works is null)
            {
                var works = await dataSource.GetPersonWorksAsync(personId, token);
                if (!_load.IsCurrent(generation, token))
                    return;

                var visibleWorks = works
                    .Where(work => work.ID > 0)
                    .DistinctBy(work => work.ID)
                    .ToList();
                var blocked = await tracking.GetBlockedAnimeIdsAsync();
                if (!_load.IsCurrent(generation, token))
                    return;

                _works = visibleWorks;
                _blocked = blocked;
                Results.Clear();
            }

            IsLoading = false;
            ResultText = $"正在加载 {_processedCount}/{_works.Count} 部作品";
            while (_processedCount < _works.Count)
            {
                token.ThrowIfCancellationRequested();
                var batch = _works.GetRange(
                    _processedCount,
                    Math.Min(DetailBatchSize, _works.Count - _processedCount));
                var loaded = await Task.WhenAll(batch.Select(work => LoadWorkAsync(work, generation, token)));
                if (!_load.IsCurrent(generation, token))
                    return;

                foreach (var anime in loaded)
                {
                    if (anime is not null && !_blocked.Contains(anime.ID))
                        Results.Add(anime);
                }

                // 只推进已发布的整批，离页时尚未完成的批次会在返回后重试。
                _processedCount += batch.Count;
                ResultText = $"正在加载 {_processedCount}/{_works.Count} 部作品";
            }

            ResultText = Results.Count == 0
                ? "未找到相关作品"
                : $"参与 {_works.Count} 部作品 · 显示 {Results.Count} 部";
        }
        catch (Exception ex) when (ex is HttpRequestException or MetadataServiceException or JsonException)
        {
            // 同一次加载可能有多个等待者；由实际查询任务报告，外层只保留原来的错误展示。
            if (_load.IsCurrent(generation, token))
                _logger.LogError(ex, "Person works load failed for {PersonId}.", personId);
            throw;
        }
        finally
        {
            if (_load.IsCurrent(generation, token))
                IsLoading = false;
        }
    }

    private async Task RefreshBlockedOnlyAsync(int generation)
    {
        try
        {
            var blocked = await tracking.GetBlockedAnimeIdsAsync();
            if (generation != _load.Generation)
                return;

            _blocked = blocked;
            for (var index = Results.Count - 1; index >= 0; index--)
            {
                if (blocked.Contains(Results[index].ID))
                    Results.RemoveAt(index);
            }

            ResultText = Results.Count == 0
                ? "未找到相关作品"
                : $"显示 {Results.Count} 部作品";
        }
#pragma warning disable CA1031 // 屏蔽刷新失败时保留已有结果，不打断页面。
        catch (Exception ex)
        {
            if (generation == _load.Generation)
                _logger.LogWarning(ex, "Blocked status refresh failed; keeping published person works.");
        }
#pragma warning restore CA1031
    }

    private async Task<Anime?> LoadWorkAsync(PersonWork work, int generation, CancellationToken token)
    {
        try
        {
            return await dataSource.GetAnimeDetailAsync(work.ID, token);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or MetadataServiceException or JsonException)
        {
            if (_load.IsCurrent(generation, token))
                _logger.LogWarning(ex, "Details unavailable for work {AnimeId}; using its person-list snapshot.", work.ID);
            return new Anime(
                work.ID,
                work.Title,
                null,
                [],
                null,
                work.CoverURL,
                work.Staff ?? string.Empty,
                0,
                0,
                null,
                null);
        }
    }
}
