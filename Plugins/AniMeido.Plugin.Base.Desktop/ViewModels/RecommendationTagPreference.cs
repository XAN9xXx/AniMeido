using AniMeido.Plugin.Base.Models;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AniMeido.Plugin.Base.ViewModels;

public partial class RecommendationTagPreference(RecommendationFeature feature,
    RecommendationAdjustment? adjustment) : ObservableObject
{
    public RecommendationFeature Feature { get; } = feature;
    public string Name => Feature.DisplayName;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(DirectionText))]
    [NotifyPropertyChangedFor(nameof(IsLiked))]
    [NotifyPropertyChangedFor(nameof(IsUnset))]
    [NotifyPropertyChangedFor(nameof(IsReduced))]
    private RecommendationAdjustment? _adjustment = adjustment;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanEdit))]
    private bool _isSaving;

    public bool CanEdit => !IsSaving;
    public bool IsLiked => Adjustment == RecommendationAdjustment.Like;
    public bool IsUnset => Adjustment is null;
    public bool IsReduced => Adjustment == RecommendationAdjustment.Reduce;

    internal void RefreshSelection()
    {
        OnPropertyChanged(nameof(IsLiked));
        OnPropertyChanged(nameof(IsUnset));
        OnPropertyChanged(nameof(IsReduced));
    }
    public string DirectionText => Adjustment switch
    {
        RecommendationAdjustment.Like => "喜欢",
        RecommendationAdjustment.Reduce => "减少推荐",
        _ => "未设置",
    };
}
