using AniMeido.Contracts;
using AniMeido.Plugin.Base.Models;
using AniMeido.Plugin.Base.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Collections.ObjectModel;

namespace AniMeido.Plugin.Base.ViewModels;

public partial class SmartConditionEditor : ObservableObject
{
    [ObservableProperty]
    private SmartListField _field = SmartListField.TrackingStatus;

    [ObservableProperty]
    private SmartListOperator _operator = SmartListOperator.Equals;

    [ObservableProperty]
    private string _value = string.Empty;

    [ObservableProperty]
    private int _groupDepth;
}

public partial class SmartListsViewModel : ObservableObject
{
    private readonly ActionCenterService _actionCenter;
    private readonly TrackingService _tracking;
    private readonly IAnimeDataSource _dataSource;

    [ObservableProperty]
    private ObservableCollection<SmartListDefinition> _definitions = [];

    [ObservableProperty]
    private ObservableCollection<SmartConditionEditor> _conditions =
    [
        new SmartConditionEditor(),
    ];

    [ObservableProperty]
    private ObservableCollection<SmartListCandidate> _results = [];

    [ObservableProperty]
    private SmartListDefinition? _selectedDefinition;

    [ObservableProperty]
    private string _name = "新智能列表";

    [ObservableProperty]
    private SmartListGroupMode _rootMode = SmartListGroupMode.All;

    [ObservableProperty]
    private SmartListGroupMode _nestedMode = SmartListGroupMode.Any;

    [ObservableProperty]
    private SmartListField _sortField = SmartListField.Title;

    [ObservableProperty]
    private bool _sortDescending;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    private bool _isPlaybackAvailable;

    [ObservableProperty]
    private ObservableCollection<SmartListField> _fields = [];

    public SmartListsViewModel(
        ActionCenterService actionCenter,
        TrackingService tracking,
        IAnimeDataSource dataSource,
        bool isPlaybackAvailable)
    {
        _actionCenter = actionCenter;
        _tracking = tracking;
        _dataSource = dataSource;
        SetPlaybackAvailability(isPlaybackAvailable);
    }

    public IReadOnlyList<SmartListOperator> Operators { get; } =
        Enum.GetValues<SmartListOperator>();

    public IReadOnlyList<SmartListGroupMode> GroupModes { get; } =
        Enum.GetValues<SmartListGroupMode>();

    public void SetPlaybackAvailability(bool isAvailable)
    {
        IsPlaybackAvailable = isAvailable;
        Fields = new ObservableCollection<SmartListField>(
            Enum.GetValues<SmartListField>().Where(field =>
                isAvailable
                || !SmartListEvaluator.IsPlaybackField(field)));
        if (isAvailable)
        {
            return;
        }

        if (SmartListEvaluator.IsPlaybackField(SortField))
        {
            SortField = SmartListField.Title;
        }

        var visibleConditions = Conditions
            .Where(condition =>
                !SmartListEvaluator.IsPlaybackField(condition.Field))
            .ToList();
        Conditions = new ObservableCollection<SmartConditionEditor>(
            visibleConditions.Count == 0
                ? [new SmartConditionEditor()]
                : visibleConditions);
        if (SelectedDefinition is not null
            && SmartListEvaluator.RequiresPlayback(SelectedDefinition))
        {
            SelectedDefinition = null;
            Results.Clear();
        }
    }

    [RelayCommand]
    public async Task LoadAsync(CancellationToken cancellationToken = default)
    {
        IsLoading = true;
        ErrorMessage = null;
        try
        {
            var definitions = await _actionCenter.GetSmartListsAsync(
                cancellationToken);
            Definitions = new ObservableCollection<SmartListDefinition>(
                definitions.Where(definition =>
                    IsPlaybackAvailable
                    || !SmartListEvaluator.RequiresPlayback(definition)));
            if (SelectedDefinition is not null)
            {
                await EvaluateAsync(cancellationToken);
            }
        }
        catch (Exception ex) when (
            ex is InvalidOperationException
            or System.Text.Json.JsonException)
        {
            ErrorMessage = ex.Message;
        }
        finally
        {
            IsLoading = false;
        }
    }

    [RelayCommand]
    private void AddCondition()
        => Conditions.Add(new SmartConditionEditor());

    [RelayCommand]
    private void RemoveCondition(SmartConditionEditor? condition)
    {
        if (condition is not null && Conditions.Count > 1)
        {
            Conditions.Remove(condition);
        }
    }

    [RelayCommand]
    public async Task SaveAsync(CancellationToken cancellationToken = default)
    {
        ErrorMessage = null;
        try
        {
            var now = DateTimeOffset.UtcNow;
            var definition = new SmartListDefinition(
                SelectedDefinition?.Id ?? Guid.NewGuid().ToString("N"),
                string.IsNullOrWhiteSpace(Name)
                    ? "未命名智能列表"
                    : Name.Trim(),
                SmartListEvaluator.SchemaVersion,
                BuildRules(),
                new SmartListSort(SortField, SortDescending),
                SelectedDefinition?.CreatedAt ?? now,
                now);
            await _actionCenter.SaveSmartListAsync(
                definition,
                cancellationToken);
            SelectedDefinition = definition;
            await LoadAsync(cancellationToken);
            await EvaluateAsync(cancellationToken);
        }
        catch (Exception ex) when (
            ex is InvalidDataException
            or InvalidOperationException)
        {
            ErrorMessage = ex.Message;
        }
    }

    [RelayCommand]
    public async Task DeleteAsync(CancellationToken cancellationToken = default)
    {
        if (SelectedDefinition is null)
        {
            return;
        }

        await _actionCenter.DeleteSmartListAsync(
            SelectedDefinition.Id,
            cancellationToken);
        SelectedDefinition = null;
        Results.Clear();
        await LoadAsync(cancellationToken);
    }

    [RelayCommand]
    public async Task EvaluateAsync(
        CancellationToken cancellationToken = default)
    {
        var definition = SelectedDefinition ?? new SmartListDefinition(
            "preview",
            Name,
            SmartListEvaluator.SchemaVersion,
            BuildRules(),
            new SmartListSort(SortField, SortDescending),
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow);
        var candidates = await BuildCandidatesAsync(cancellationToken);
        Results = new ObservableCollection<SmartListCandidate>(
            SmartListEvaluator.Apply(definition, candidates));
    }

    partial void OnSelectedDefinitionChanged(
        SmartListDefinition? value)
    {
        if (value is null)
        {
            return;
        }

        Name = value.Name;
        RootMode = value.Rules.Mode;
        SortField = value.Sort?.Field ?? SmartListField.Title;
        SortDescending = value.Sort?.Descending == true;
        var editors = value.Rules.Conditions.Select(condition =>
            ToEditor(condition, 0)).ToList();
        if (value.Rules.Groups?.FirstOrDefault() is { } nested)
        {
            NestedMode = nested.Mode;
            editors.AddRange(nested.Conditions.Select(condition =>
                ToEditor(condition, 1)));
        }
        Conditions = new ObservableCollection<SmartConditionEditor>(
            editors.Count == 0
                ? [new SmartConditionEditor()]
                : editors);
        _ = EvaluateAsync();
    }

    private SmartListRuleGroup BuildRules()
    {
        var root = Conditions
            .Where(item => item.GroupDepth == 0)
            .Select(ToCondition)
            .ToList();
        var nested = Conditions
            .Where(item => item.GroupDepth == 1)
            .Select(ToCondition)
            .ToList();
        return new SmartListRuleGroup(
            RootMode,
            root,
            nested.Count == 0
                ? null
                : [new SmartListRuleGroup(NestedMode, nested)]);
    }

    private async Task<IReadOnlyList<SmartListCandidate>>
        BuildCandidatesAsync(CancellationToken cancellationToken)
    {
        var tracking = await _tracking.GetAllTrackingAsync();
        var plans = await _actionCenter.GetPlansAsync(
            includeArchived: true,
            cancellationToken);
        IReadOnlyDictionary<int, AnimeProgressSnapshot> progress =
            IsPlaybackAvailable
                ? await _actionCenter.GetProgressAsync(cancellationToken)
                : new Dictionary<int, AnimeProgressSnapshot>();
        var planById = plans.ToDictionary(item => item.AnimeId);
        var ids = tracking.Select(item => item.AnimeId)
            .Concat(plans.Select(item => item.AnimeId))
            .Concat(progress.Keys)
            .Distinct()
            .ToList();
        var candidates = new List<SmartListCandidate>();
        foreach (var animeId in ids)
        {
            var anime = await _dataSource.GetAnimeDetailAsync(
                animeId,
                cancellationToken);
            if (anime is null)
            {
                continue;
            }

            var tags = await _dataSource.GetTagsAsync(
                animeId,
                cancellationToken);
            var trackingRow = tracking.FirstOrDefault(
                item => item.AnimeId == animeId);
            planById.TryGetValue(animeId, out var plan);
            progress.TryGetValue(animeId, out var snapshot);
            var targetDate = plan?.TargetStartDate;
            candidates.Add(new SmartListCandidate(
                animeId,
                anime.Title,
                trackingRow.AnimeId == 0
                    ? null
                    : (int)trackingRow.Status,
                snapshot?.CurrentEpisode ?? 0,
                snapshot is not null
                    && snapshot.DurationSeconds > 0
                    && snapshot.PositionSeconds
                        / snapshot.DurationSeconds < 0.9,
                plan is null ? null : (int)plan.Priority,
                targetDate,
                targetDate is not null
                    && targetDate < DateOnly.FromDateTime(DateTime.Today)
                    && plan?.ArchivedAt is null,
                anime.Weekday,
                anime.AirDate,
                tags.Select(item => item.Name).ToList(),
                snapshot?.LastWatchedAt,
                trackingRow.AnimeId == 0
                    ? null
                    : DateTimeOffset.TryParse(
                        trackingRow.UpdatedAt,
                        out var updated)
                        ? updated
                        : null));
        }

        return candidates;
    }

    private static SmartListCondition ToCondition(
        SmartConditionEditor editor)
        => new(editor.Field, editor.Operator, editor.Value);

    private static SmartConditionEditor ToEditor(
        SmartListCondition condition,
        int depth)
        => new()
        {
            Field = condition.Field,
            Operator = condition.Operator,
            Value = condition.Value ?? string.Empty,
            GroupDepth = depth,
        };
}
