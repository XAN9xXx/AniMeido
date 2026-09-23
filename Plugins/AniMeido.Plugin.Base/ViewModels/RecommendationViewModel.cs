using AniMeido.Contracts.Models;
using AniMeido.Plugin.Base.Models;
using AniMeido.Plugin.Base.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using System.Collections.ObjectModel;

namespace AniMeido.Plugin.Base.ViewModels;

public partial class RecommendationTagOption(string name) : ObservableObject
{
    public string Name { get; } = name;

    [ObservableProperty]
    private bool _isSelected;
}

public partial class RecommendationViewModel : ObservableObject
{
    private const int OnboardingTagBatchSize = 12;
    private static readonly IReadOnlyList<string> OnboardingTags = RecommendationTagCatalog.Tags;

    private readonly RecommendationService _recommendations;
    private readonly RecommendationBrowseState _browse;
    private readonly Dictionary<int, Task<IReadOnlyList<RecommendationFeature>>> _tagRequests = [];
    private int _selectionGeneration;
    private int _tagLoadGeneration;
    private int _preferenceGeneration;
    private int _loadGeneration;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    private RecommendationItem? _selectedItem;

    [ObservableProperty]
    private ObservableCollection<RecommendationTagPreference> _selectedTags = [];

    [ObservableProperty]
    private bool _isLoadingTags;

    [ObservableProperty]
    private string? _tagError;

    [ObservableProperty]
    private string _followLabel = "+ 关注";

    private readonly HashSet<int> _savingFollowingIds = [];
    public IReadOnlySet<int> SavingFollowingIds => _savingFollowingIds;

    private bool _isSavingPreference;
    public bool HasSelection => SelectedItem is not null;
    public RecommendationBrowseState BrowseState => _browse;
    public string TagSummary => $"标签偏好 · {SelectedTags.Count} 个 · 已设置 {SelectedTags.Count(tag => tag.Adjustment is not null)} 项";

    partial void OnSelectedItemChanged(RecommendationItem? value)
    {
        _browse.SelectedAnimeId = value?.Anime.ID;
        _selectionGeneration++;
        SelectedTags = new(value?.Reasons.Where(reason => reason.Feature.Kind == RecommendationFeatureKind.Tag)
            .DistinctBy(reason => reason.Feature.Key)
            .Select(reason => new RecommendationTagPreference(reason.Feature,
                Profile.FirstOrDefault(profile => profile.Feature.Kind == RecommendationFeatureKind.Tag
                    && profile.Feature.Key == reason.Feature.Key)?.Adjustment)) ?? []);
        TagError = null;
        IsLoadingTags = false;
        FollowLabel = value is not null && _browse.TrackingLabels.TryGetValue(value.Anime.ID, out var label)
            ? label : "+ 关注";
        OnPropertyChanged(nameof(TagSummary));
    }

    /// <summary>
    /// 页面离开时调用：作废进行中的请求。旧请求收尾时代数已不符，不会复位加载状态，所以在这里复位；
    /// 被打断的是整页加载或刷新时记下来，页面回来后重新加载。
    /// </summary>
    public void Suspend()
    {
        var interrupted = IsBusy || IsRefreshing;
        _resumeNeedsReload |= interrupted;
        _selectionGeneration++;
        _loadGeneration++;
        _refreshGeneration++;
        _tagRequests.Clear();
        // 画像还没读出过时不能用初始的空画像覆盖已保存的画像；读出之后即使刷新被打断也要写回，
        // 返回后的快速恢复只从 _browse.Profile 取画像。
        if (_profileLoaded)
            _browse.Profile = Profile.ToArray();
        IsBusy = false;
        IsRefreshing = false;
        IsLoadingTags = false;
    }

    /// <summary>离开前的整页加载或刷新是否被打断；读取后清除。</summary>
    public bool TakeResumeReload()
    {
        var needsReload = _resumeNeedsReload;
        _resumeNeedsReload = false;
        return needsReload;
    }

    public async Task LoadSelectedTagsAsync(CancellationToken cancellationToken = default)
    {
        var item = SelectedItem;
        if (item is null) return;
        var generation = _selectionGeneration;
        var tagGeneration = ++_tagLoadGeneration;
        bool IsCurrent() => generation == _selectionGeneration && tagGeneration == _tagLoadGeneration
            && !cancellationToken.IsCancellationRequested;
        IsLoadingTags = true;
        TagError = null;
        Task<IReadOnlyList<RecommendationFeature>>? request = null;
        try
        {
            var status = await _recommendations.GetTrackingStatusAsync(item.Anime.ID);
            if (!IsCurrent()) return;
            FollowLabel = status switch
            {
                null => "+ 关注",
                AnimeTrackingStatus.Following => "已关注",
                _ => "已有标记",
            };
            if (status is null) _browse.TrackingLabels.Remove(item.Anime.ID);
            else _browse.TrackingLabels[item.Anime.ID] = FollowLabel;
            if (!_tagRequests.TryGetValue(item.Anime.ID, out request))
            {
                request = _recommendations.GetPreviewTagsAsync(item.Anime.ID, cancellationToken);
                _tagRequests[item.Anime.ID] = request;
            }
            var tags = await request;
            var preferenceGeneration = _preferenceGeneration;
            var preferences = await _recommendations.GetFeaturePreferencesAsync(cancellationToken);
            if (!IsCurrent()) return;
            // A completed save wins over a preference read started before that save.
            if (preferenceGeneration != _preferenceGeneration)
                preferences = await _recommendations.GetFeaturePreferencesAsync(cancellationToken);
            if (!IsCurrent()) return;
            var reasonKeys = item.Reasons.Select(reason => reason.Feature.Key).ToHashSet();
            SelectedTags = new(tags.OrderByDescending(tag => reasonKeys.Contains(tag.Key))
                .ThenBy(tag => tag.DisplayName, StringComparer.Ordinal)
                .Select(tag => new RecommendationTagPreference(tag,
                    preferences.FirstOrDefault(preference => preference.Kind == tag.Kind
                        && preference.Key == tag.Key)?.Adjustment)));
            OnPropertyChanged(nameof(TagSummary));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex) when (IsExpectedFailure(ex))
        {
            if (IsCurrent()) TagError = $"标签加载失败，保留已知标签。{ex.Message}";
        }
        finally
        {
            // Never retain a failed task, or let an old request remove a newer retry.
            if (request is { IsCompletedSuccessfully: false }
                && _tagRequests.TryGetValue(item.Anime.ID, out var cached)
                && ReferenceEquals(request, cached))
                _tagRequests.Remove(item.Anime.ID);
            if (IsCurrent()) IsLoadingTags = false;
        }
    }

    public async Task SaveTagAsync(RecommendationTagPreference tag,
        RecommendationAdjustment? adjustment, CancellationToken cancellationToken)
    {
        if (tag.IsSaving || _isSavingPreference) return;
        tag.IsSaving = true;
        try
        {
            await SetPreferenceAsync(tag.Feature, adjustment, cancellationToken);
            if (!cancellationToken.IsCancellationRequested) tag.Adjustment = adjustment;
        }
        finally
        {
            tag.IsSaving = false;
            OnPropertyChanged(nameof(TagSummary));
        }
    }

    public async Task ToggleFollowingAsync(RecommendationItem item, CancellationToken cancellationToken)
    {
        if (!_savingFollowingIds.Add(item.Anime.ID)) return;
        OnPropertyChanged(nameof(SavingFollowingIds));
        try
        {
            var (status, changed) = await _recommendations.ToggleFollowingAsync(item.Anime.ID, cancellationToken);
            if (cancellationToken.IsCancellationRequested) return;
            var label = status switch
            {
                null => "+ 关注",
                AnimeTrackingStatus.Following => "已关注",
                _ => "已有标记",
            };
            if (status is null) _browse.TrackingLabels.Remove(item.Anime.ID);
            else _browse.TrackingLabels[item.Anime.ID] = label;
            if (SelectedItem?.Anime.ID == item.Anime.ID) FollowLabel = label;
            Message = status switch
            {
                null when changed => "已取消关注，当前列表保留。",
                AnimeTrackingStatus.Following when changed => "已关注，当前列表保留。",
                _ => "此作品已有其他标记，未覆盖。",
            };
        }
        finally
        {
            _savingFollowingIds.Remove(item.Anime.ID);
            OnPropertyChanged(nameof(SavingFollowingIds));
        }
    }

    public void Skip(RecommendationItem item)
    {
        _browse.SkippedIds.Add(item.Anime.ID);
        RemoveItem(item);
        Message = "本轮暂时跳过；生成新一轮后可再次参与推荐。";
    }

    private void RemoveItem(RecommendationItem item)
    {
        var index = Items.IndexOf(item);
        var wasSelected = SelectedItem?.Anime.ID == item.Anime.ID;
        Items.Remove(item);
        if (wasSelected)
        {
            var next = RecommendationBrowseState.SelectAfterRemoval(Items, index);
            SelectedItem = Items.FirstOrDefault(candidate => candidate.Anime.ID == next);
        }
        OnPropertyChanged(nameof(HasItems));
    }

    private int _refreshGeneration;
    private int _onboardingTagOffset;
    private bool _resumeNeedsReload;
    private bool _profileLoaded;

    [ObservableProperty]
    private ObservableCollection<RecommendationItem> _items = [];

    [ObservableProperty]
    private ObservableCollection<RecommendationFeatureProfile> _profile = [];

    // 字段初始值只是空占位；被真实画像赋值过之后，离开页面时才写回浏览状态。
    partial void OnProfileChanged(ObservableCollection<RecommendationFeatureProfile> value) => _profileLoaded = true;

    [ObservableProperty]
    private ObservableCollection<RecommendationHiddenAnime> _hiddenAnime = [];

    [ObservableProperty]
    private ObservableCollection<RecommendationTagOption> _suggestedTags = [];

    [ObservableProperty]
    private bool _isBusy;

    [ObservableProperty]
    private bool _isRefreshing;

    [ObservableProperty]
    private bool _isPersonalized;

    [ObservableProperty]
    private string _snapshotText = "尚未生成推荐";

    [ObservableProperty]
    private string? _message;

    [ObservableProperty]
    private bool _hasError;

    public RecommendationViewModel(RecommendationService recommendations, RecommendationBrowseState browse)
    {
        _recommendations = recommendations;
        _browse = browse;
        RefreshSuggestedTags();
    }

    public IReadOnlyList<string> SelectedOnboardingTags => SuggestedTags
        .Where(option => option.IsSelected)
        .Select(option => option.Name)
        .ToArray();

    public bool IsColdStart => Profile.Count == 0;

    public bool HasItems => Items.Count > 0;

    public bool HasHiddenAnime => HiddenAnime.Count > 0;

    public void ReportError(string message)
        => ShowError(message);

    public void RefreshSuggestedTags()
    {
        var selected = SuggestedTags
            .Where(option => option.IsSelected)
            .ToList();
        var selectedNames = selected
            .Select(option => option.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var candidates = Enumerable.Range(0, OnboardingTags.Count)
            .Select(index => OnboardingTags[
                (_onboardingTagOffset + index) % OnboardingTags.Count])
            .Where(tag => !selectedNames.Contains(tag))
            .Take(OnboardingTagBatchSize - selected.Count)
            .Select(tag => new RecommendationTagOption(tag));
        SuggestedTags = new(selected.Concat(candidates));
        _onboardingTagOffset = (
            _onboardingTagOffset + OnboardingTagBatchSize)
            % OnboardingTags.Count;
    }

    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        var generation = Interlocked.Increment(ref _loadGeneration);
        Interlocked.Increment(ref _refreshGeneration);
        IsRefreshing = false;
        IsBusy = true;
        ClearMessage();
        try
        {
            if (_browse.Snapshot is { SchemaVersion: RecommendationSnapshot.CurrentSchemaVersion } saved)
            {
                var selectedId = _browse.SelectedAnimeId;
                Profile = new(_browse.Profile);
                Items = new(_browse.VisibleItems);
                SelectedItem = Items.FirstOrDefault(item => item.Anime.ID == selectedId) ?? Items.FirstOrDefault();
                IsPersonalized = saved.IsPersonalized;
                SnapshotText = $"{saved.GeneratedAt.ToLocalTime():M月d日 HH:mm} 更新";
                await LoadHiddenAsync(cancellationToken);
                if (!IsCurrentLoad(generation, cancellationToken)) return;
                OnPropertyChanged(nameof(HasItems));
                OnPropertyChanged(nameof(IsColdStart));
                if (_browse.HasPendingPreferences) Message = "偏好已更新，下轮推荐生效。";
                if (!RecommendationService.IsSnapshotFresh(saved, DateTimeOffset.UtcNow))
                {
                    // RefreshAsync republishes Profile on success; on failure the
                    // restored browse profile stays, because it is the only copy
                    // that carries this session's manual preference edits.
                    await RefreshAsync(cancellationToken);
                }
                return;
            }
            var validSnapshot = await _recommendations.GetCachedSnapshotAsync(
                allowExpired: false,
                cancellationToken);
            var snapshot = validSnapshot ?? await _recommendations
                .GetCachedSnapshotAsync(
                    allowExpired: true,
                    cancellationToken);
            if (!IsCurrentLoad(generation, cancellationToken))
            {
                return;
            }

            if (snapshot is not null)
            {
                ApplySnapshot(snapshot);
            }

            await LoadPersonalDataAsync(generation, cancellationToken);
            if (!IsCurrentLoad(generation, cancellationToken))
            {
                return;
            }

            if (validSnapshot is null)
            {
                await RefreshAsync(cancellationToken);
            }
        }
        catch (OperationCanceledException) when (
            cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (IsExpectedFailure(ex))
        {
            if (generation == _loadGeneration)
            {
                ShowError($"推荐加载失败：{ex.Message}");
            }
        }
        finally
        {
            if (generation == _loadGeneration)
            {
                IsBusy = false;
            }
        }
    }

    public async Task RefreshAsync(
        CancellationToken cancellationToken = default,
        bool preferNewBatch = false)
    {
        var generation = Interlocked.Increment(ref _refreshGeneration);
        var previousIds = Items.Select(item => item.Anime.ID).ToHashSet();
        IsRefreshing = true;
        ClearMessage();
        try
        {
            var result = await _recommendations.RefreshAsync(
                cancellationToken,
                preferNewBatch,
                preferNewBatch ? previousIds : null);
            if (generation != _refreshGeneration || cancellationToken.IsCancellationRequested)
            {
                return;
            }

            ApplySnapshot(result.Snapshot);
            Profile = new(result.Profile.OrderByDescending(
                item => item.EffectiveScore));
            _browse.Profile = Profile.ToArray();
            OnPropertyChanged(nameof(IsColdStart));
            var hasDifferentItems = result.Snapshot.Items
                .Select(item => item.Anime.ID)
                .Any(id => !previousIds.Contains(id));
            Message = preferNewBatch && previousIds.Count > 0
                ? hasDifferentItems
                    ? "已优先换入上一批未展示的作品。"
                    : "当前候选有限，暂无更多不同结果。"
                : result.Snapshot.IsPersonalized
                ? "推荐已根据本地偏好更新。"
                : "当前数据较少，暂时显示热门推荐。";
        }
        catch (OperationCanceledException) when (
            cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex) when (IsExpectedFailure(ex))
        {
            ShowError(Items.Count > 0
                ? $"刷新失败，已保留上次结果：{ex.Message}"
                : $"推荐生成失败：{ex.Message}");
        }
        finally
        {
            if (generation == _refreshGeneration)
            {
                IsRefreshing = false;
            }
        }
    }

    public async Task HideAsync(
        RecommendationItem item,
        CancellationToken cancellationToken = default)
    {
        await _recommendations.HideAnimeAsync(
            item.Anime.ID,
            item.Anime.Title,
            cancellationToken);
        _browse.RemovedIds.Add(item.Anime.ID);
        if (cancellationToken.IsCancellationRequested) return;
        RemoveItem(item);
        await LoadHiddenAsync(cancellationToken);
        OnPropertyChanged(nameof(HasItems));
        Message = "已从推荐中隐藏，可在“已隐藏”中恢复。";
    }

    public async Task MarkNotInterestedAsync(
        RecommendationItem item,
        CancellationToken cancellationToken = default)
    {
        await _recommendations.MarkNotInterestedAsync(
            item.Anime.ID,
            cancellationToken);
        _browse.RemovedIds.Add(item.Anime.ID);
        if (cancellationToken.IsCancellationRequested) return;
        RemoveItem(item);
        Message = "已标记为不感兴趣，不会再出现在推荐中。";
    }

    public async Task RestoreAsync(
        RecommendationHiddenAnime item,
        CancellationToken cancellationToken = default)
    {
        await _recommendations.RestoreAnimeAsync(
            item.AnimeId,
            cancellationToken);
        HiddenAnime.Remove(item);
        OnPropertyChanged(nameof(HasHiddenAnime));
        Message = "已恢复。刷新后该作品可能重新出现。";
    }

    public async Task ClearHiddenAsync(
        CancellationToken cancellationToken = default)
    {
        await _recommendations.ClearHiddenAnimeAsync(cancellationToken);
        HiddenAnime.Clear();
        OnPropertyChanged(nameof(HasHiddenAnime));
        Message = "已恢复全部隐藏作品。";
    }

    public async Task SetPreferenceAsync(
        RecommendationFeature feature,
        RecommendationAdjustment? adjustment,
        CancellationToken cancellationToken = default)
    {
        if (_isSavingPreference) return;
        _isSavingPreference = true;
        try
        {
            await _recommendations.SetFeaturePreferenceAsync(feature, adjustment, cancellationToken);
            _preferenceGeneration++;
            _browse.HasPendingPreferences = true;
            // Preserve the current evidence and ordering; only explicit preference state changes.
            var existing = Profile.FirstOrDefault(item => item.Feature.Kind == feature.Kind && item.Feature.Key == feature.Key);
            var updated = existing is null
                ? new RecommendationFeatureProfile(feature, 0, adjustment, [])
                : existing with { Adjustment = adjustment };
            var profile = Profile.ToList();
            if (existing is not null) profile[profile.IndexOf(existing)] = updated;
            else profile.Add(updated);
            _browse.Profile = profile;
            if (cancellationToken.IsCancellationRequested) return;
            Profile = new(profile);
            foreach (var tag in SelectedTags.Where(tag => tag.Feature.Kind == feature.Kind && tag.Feature.Key == feature.Key))
                tag.Adjustment = adjustment;
            OnPropertyChanged(nameof(TagSummary));
            Message = "偏好已更新，下轮推荐生效。";
            HasError = false;
        }
        finally { _isSavingPreference = false; }
    }

    public async Task ApplyOnboardingTagsAsync(
        IEnumerable<string> displayNames,
        CancellationToken cancellationToken = default)
    {
        var tags = displayNames
            .Where(tag => !string.IsNullOrWhiteSpace(tag))
            .Select(tag => tag.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        if (tags.Length == 0)
        {
            return;
        }

        foreach (var tag in tags)
        {
            await _recommendations.SetFeaturePreferenceAsync(
                new RecommendationFeature(
                    RecommendationFeatureKind.Tag,
                    RecommendationCandidateProvider.NormalizeTag(tag),
                    tag),
                RecommendationAdjustment.Like,
                cancellationToken);
        }

        await RefreshAsync(cancellationToken);
    }

    public async Task ClearPreferencesAsync(
        CancellationToken cancellationToken = default)
    {
        await _recommendations.ClearFeaturePreferencesAsync(
            cancellationToken);
        await RefreshAsync(cancellationToken);
    }

    private async Task LoadPersonalDataAsync(
        int generation,
        CancellationToken cancellationToken)
    {
        var hidden = await _recommendations.GetHiddenAnimeAsync(
            cancellationToken);
        if (!IsCurrentLoad(generation, cancellationToken))
        {
            return;
        }

        HiddenAnime = new(hidden);
        OnPropertyChanged(nameof(HasHiddenAnime));
        if (_recommendations.LastProfile.Count > 0)
        {
            Profile = new(_recommendations.LastProfile.OrderByDescending(
                item => item.EffectiveScore));
        }
        else
        {
            var preferences = await _recommendations
                .GetFeaturePreferencesAsync(cancellationToken);
            if (!IsCurrentLoad(generation, cancellationToken))
            {
                return;
            }

            Profile = new(preferences.Select(item =>
                new RecommendationFeatureProfile(
                    new RecommendationFeature(
                        item.Kind,
                        item.Key,
                        item.DisplayName),
                    0,
                    item.Adjustment,
                    [])));
        }

        OnPropertyChanged(nameof(IsColdStart));
    }

    private bool IsCurrentLoad(
        int generation,
        CancellationToken cancellationToken)
        => generation == _loadGeneration
            && !cancellationToken.IsCancellationRequested;

    private async Task LoadHiddenAsync(CancellationToken cancellationToken)
    {
        HiddenAnime = new(await _recommendations.GetHiddenAnimeAsync(
            cancellationToken));
        OnPropertyChanged(nameof(HasHiddenAnime));
    }

    private void ApplySnapshot(RecommendationSnapshot snapshot)
    {
        var selectedId = _browse.SelectedAnimeId;
        _tagRequests.Clear();
        _browse.StartRound(snapshot);
        Items = new(snapshot.Items);
        SelectedItem = Items.FirstOrDefault(item => item.Anime.ID == selectedId) ?? Items.FirstOrDefault();
        IsPersonalized = snapshot.IsPersonalized;
        SnapshotText = $"{snapshot.GeneratedAt.ToLocalTime():M月d日 HH:mm} 更新";
        OnPropertyChanged(nameof(HasItems));
    }

    private void ClearMessage()
    {
        Message = null;
        HasError = false;
    }

    private void ShowError(string message)
    {
        Message = message;
        HasError = true;
    }

    private static bool IsExpectedFailure(Exception exception)
        => exception is InvalidOperationException
            or HttpRequestException
            or OperationCanceledException
            or Microsoft.Data.Sqlite.SqliteException
            or System.Text.Json.JsonException;
}
