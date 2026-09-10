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
    private static readonly string[] OnboardingTags =
    [
        "科幻", "奇幻", "恋爱", "日常", "喜剧", "动作",
        "悬疑", "治愈", "校园", "音乐", "运动", "冒险",
        "机器人", "青春", "历史", "推理", "战斗", "魔法",
        "家庭", "职场", "旅行", "美食", "美术", "萌系",
        "偶像", "公路片", "时空穿越", "超能力", "游戏", "社会",
        "剧情", "原创", "漫画改", "小说改", "群像", "成长",
    ];

    private readonly RecommendationService _recommendations;
    private int _loadGeneration;
    private int _refreshGeneration;
    private int _onboardingTagOffset;

    [ObservableProperty]
    private ObservableCollection<RecommendationItem> _items = [];

    [ObservableProperty]
    private ObservableCollection<RecommendationFeatureProfile> _profile = [];

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

    public RecommendationViewModel(RecommendationService recommendations)
    {
        _recommendations = recommendations;
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
        var candidates = Enumerable.Range(0, OnboardingTags.Length)
            .Select(index => OnboardingTags[
                (_onboardingTagOffset + index) % OnboardingTags.Length])
            .Where(tag => !selectedNames.Contains(tag))
            .Take(OnboardingTagBatchSize - selected.Count)
            .Select(tag => new RecommendationTagOption(tag));
        SuggestedTags = new(selected.Concat(candidates));
        _onboardingTagOffset = (
            _onboardingTagOffset + OnboardingTagBatchSize)
            % OnboardingTags.Length;
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
            if (generation != _refreshGeneration)
            {
                return;
            }

            ApplySnapshot(result.Snapshot);
            Profile = new(result.Profile.OrderByDescending(
                item => item.EffectiveScore));
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
        Items.Remove(item);
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
        Items.Remove(item);
        OnPropertyChanged(nameof(HasItems));
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
        await _recommendations.SetFeaturePreferenceAsync(
            feature,
            adjustment,
            cancellationToken);
        await RefreshAsync(cancellationToken);
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
        Items = new(snapshot.Items);
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
            or Microsoft.Data.Sqlite.SqliteException
            or System.Text.Json.JsonException;
}
