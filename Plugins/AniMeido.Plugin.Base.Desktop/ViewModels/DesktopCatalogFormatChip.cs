using AniMeido.Contracts.Models;
using CommunityToolkit.Mvvm.ComponentModel;

namespace AniMeido.Plugin.Base.ViewModels;

public sealed partial class DesktopCatalogFormatChip(AnimeMediaFormat? format, string label) : ObservableObject
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanSelect))]
    private PastSeasonFormatChip _chip = new(format, label, 0, format is null);

    public bool CanSelect => Chip.Format is null || Chip.Count > 0 || Chip.IsSelected;
}
