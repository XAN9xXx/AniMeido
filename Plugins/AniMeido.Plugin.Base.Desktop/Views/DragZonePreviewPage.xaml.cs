using AniMeido.Contracts.Models;
using Microsoft.UI.Xaml.Controls;

namespace AniMeido.Plugin.Base.Views;

public sealed partial class DragZonePreviewPage : Page
{
    public IReadOnlyList<Anime> Samples { get; } = Enumerable.Range(1, 4)
        .Select(id => new Anime(-id, $"示例番剧 {id}", null, [], null, null, "", 0, 0, null)).ToArray();
    public DragZonePreviewPage() => InitializeComponent();
}
