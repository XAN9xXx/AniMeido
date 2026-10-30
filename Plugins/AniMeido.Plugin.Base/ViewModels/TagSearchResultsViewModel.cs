using AniMeido.Contracts.Models;
using AniMeido.Plugin.Base.Exceptions;
using AniMeido.Plugin.Base.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using System.Collections.ObjectModel;
using System.Text.Json;

namespace AniMeido.Plugin.Base.ViewModels;

/// <summary>标签结果的查询、可见性、排序与分页状态，不持有页面控件。</summary>
public sealed partial class TagSearchResultsViewModel : ObservableObject
{
    private const int PageSize = 20;
    private const int MaximumYearSpan = 10;
    private readonly Func<string, int, int, CancellationToken, Task<List<Anime>>> _load;
    private readonly Func<Task<HashSet<int>>> _readBlocked;
    private readonly ILogger _logger;
    private string? _tag;
    private int _fromYear;
    private int _toYear;
    private string _sort = "rank";
    private bool _descending = true;
    private List<Anime>? _rawData;
    private List<Anime>? _allData;
    private HashSet<int> _blockedIds = [];
    private CancellationTokenSource? _searchCancellation;
    private int _searchVersion;
    private Task? _inflight;
    private bool _isSuspended;
    private bool _resumeNeedsReload;

    public TagSearchResultsViewModel(
        TagSearchResults search,
        TrackingService tracking,
        ILogger logger)
        : this(search.LoadAsync, tracking.GetBlockedAnimeIdsAsync, logger)
    {
    }

    internal TagSearchResultsViewModel(
        Func<string, int, int, CancellationToken, Task<List<Anime>>> load,
        Func<Task<HashSet<int>>> readBlocked,
        ILogger logger)
    {
        _load = load;
        _readBlocked = readBlocked;
        _logger = logger;
    }

    public ObservableCollection<Anime> Items { get; } = [];
    public int TotalCount => _allData?.Count ?? 0;
    public bool CanGoPrevious => HasPages && !IsLoading && CurrentOffset > 0;
    public bool CanGoNext => HasPages && !IsLoading && CurrentOffset + PageSize < TotalCount;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanGoPrevious))]
    [NotifyPropertyChangedFor(nameof(CanGoNext))]
    private int _currentOffset;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanGoPrevious))]
    [NotifyPropertyChangedFor(nameof(CanGoNext))]
    private bool _isLoading;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanGoPrevious))]
    [NotifyPropertyChangedFor(nameof(CanGoNext))]
    private bool _hasPages;

    [ObservableProperty]
    private string _resultText = string.Empty;

    [ObservableProperty]
    private string _pageText = string.Empty;

    public Task InitializeAsync(string tag, int fromYear, int toYear)
    {
        _tag = tag;
        _sort = "rank";
        _descending = true;
        return ChangeYearRangeAsync(fromYear, toYear);
    }

    public Task ChangeYearRangeAsync(int fromYear, int toYear)
    {
        _fromYear = fromYear;
        _toYear = toYear;
        CurrentOffset = 0;
        return LoadAsync();
    }

    public void SetSort(string sort, bool descending)
    {
        if (_allData is null)
            return;

        _sort = sort;
        _descending = descending;
        ApplyPresentation();
        ShowPage(0);
    }

    public void NextPage()
    {
        if (CanGoNext)
            ShowPage(CurrentOffset + PageSize);
    }

    public void PreviousPage()
    {
        if (CanGoPrevious)
            ShowPage(CurrentOffset - PageSize);
    }

    public void Suspend()
    {
        _resumeNeedsReload |= IsLoading;
        _isSuspended = true;
        _searchVersion++;
        var cancellation = _searchCancellation;
        _searchCancellation = null;
        _inflight = null;
        IsLoading = false;
        // 请求自己在 finally 中释放 CTS；离页只作废所有权并发出取消。
        cancellation?.Cancel();
    }

    public Task ResumeAsync()
    {
        if (_inflight is { IsCompleted: false })
            return _inflight;
        if (!_isSuspended || _tag is null)
            return Task.CompletedTask;

        _isSuspended = false;
        return _resumeNeedsReload
            ? LoadAsync()
            : TrackRequest(RefreshBlockedIdsAsync(_searchVersion));
    }

    private Task LoadAsync()
    {
        _isSuspended = false;
        _resumeNeedsReload = false;
        return TrackRequest(LoadCoreAsync());
    }

    private Task TrackRequest(Task request)
    {
        _inflight = request.IsCompleted ? null : request;
        return request;
    }

    private async Task LoadCoreAsync()
    {
        if (_tag is null)
            return;

        _searchCancellation?.Cancel();
        var cancellation = new CancellationTokenSource();
        _searchCancellation = cancellation;
        var token = cancellation.Token;
        var version = ++_searchVersion;
        ClearResults();
        IsLoading = true;

        try
        {
            if (_fromYear > _toYear)
            {
                ResultText = "起始年份不能晚于结束年份";
                return;
            }
            if (_toYear - _fromYear > MaximumYearSpan)
            {
                ResultText = $"年份跨度超过 {MaximumYearSpan} 年，请缩小搜索范围";
                return;
            }

            await RefreshBlockedIdsAsync(version);
            token.ThrowIfCancellationRequested();
            var results = await _load(_tag, _fromYear, _toYear, token);
            token.ThrowIfCancellationRequested();
            if (version != _searchVersion)
                return;

            _rawData = results;
            ApplyPresentation();
            ShowPage(0);
        }
        catch (HttpRequestException ex)
        {
            PublishFailure(version, $"搜索失败：{ex.Message}", ex);
        }
        catch (MetadataServiceException ex)
        {
            PublishFailure(version, $"数据源请求失败：{ex.Message}", ex);
        }
        catch (JsonException ex)
        {
            PublishFailure(version, $"搜索结果解析失败：{ex.Message}", ex);
        }
        catch (OperationCanceledException)
        {
            if (version == _searchVersion)
                HasPages = false;
        }
        finally
        {
            if (ReferenceEquals(_searchCancellation, cancellation))
                _searchCancellation = null;
            cancellation.Dispose();
            if (version == _searchVersion)
            {
                _inflight = null;
                IsLoading = false;
            }
        }
    }

    private async Task RefreshBlockedIdsAsync(int version)
    {
        try
        {
            var blocked = await _readBlocked();
            if (version != _searchVersion)
                return;

            var changed = !_blockedIds.SetEquals(blocked);
            _blockedIds = blocked;
            if (changed && _rawData is not null)
            {
                ApplyPresentation();
                ShowPage(CurrentOffset);
            }
        }
#pragma warning disable CA1031 // 屏蔽列表刷新失败不丢弃已经取得的作品。
        catch (Exception ex)
        {
            if (version == _searchVersion)
                _logger.LogWarning(ex, "Blocked status refresh failed; keeping current tag results.");
        }
#pragma warning restore CA1031
    }

    private void ApplyPresentation()
    {
        _allData = TagSearchResults.Sort(
            AnimeListPresentation.Filter(_rawData ?? [], _blockedIds).ToList(),
            _sort,
            _descending);
        OnPropertyChanged(nameof(TotalCount));
        OnPropertyChanged(nameof(CanGoNext));
    }

    private void ShowPage(int offset)
    {
        Items.Clear();
        if (TotalCount == 0)
        {
            CurrentOffset = 0;
            PageText = string.Empty;
            ResultText = "未找到相关番剧";
            HasPages = false;
            return;
        }

        var lastPageOffset = (TotalCount - 1) / PageSize * PageSize;
        offset = Math.Clamp(offset, 0, lastPageOffset);
        CurrentOffset = offset;
        foreach (var anime in _allData!.Skip(offset).Take(PageSize))
            Items.Add(anime);

        var currentPage = offset / PageSize + 1;
        var totalPages = (TotalCount + PageSize - 1) / PageSize;
        ResultText = $"找到 {TotalCount} 部番剧 · 第 {currentPage}/{totalPages} 页";
        PageText = $"{currentPage} / {totalPages}";
        HasPages = true;
    }

    private void PublishFailure(int version, string message, Exception exception)
    {
        if (version != _searchVersion)
            return;

        _logger.LogError(exception, "Tag result query failed for years {FromYear} to {ToYear}.", _fromYear, _toYear);
        _resumeNeedsReload = true;
        ClearResults();
        ResultText = message;
    }

    private void ClearResults()
    {
        _rawData = null;
        _allData = null;
        OnPropertyChanged(nameof(TotalCount));
        OnPropertyChanged(nameof(CanGoNext));
        Items.Clear();
        HasPages = false;
    }
}
