using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace AniMeido.Plugin.Base.Views;

public sealed partial class ManagementPage
{
    private bool? _desktopMineToolsStacked;

    private void OnDesktopManagementSizeChanged(object sender, SizeChangedEventArgs args)
    {
        if (args.NewSize.Width <= 0) return;
        var stacked = args.NewSize.Width < 900;
        if (_desktopMineToolsStacked == stacked) return;
        _desktopMineToolsStacked = stacked;
        Grid.SetRow(ViewTools, stacked ? 1 : 0);
        Grid.SetColumn(ViewTools, stacked ? 0 : 1);
        Grid.SetColumnSpan(ViewTools, stacked ? 2 : 1);
        Grid.SetColumnSpan(SearchBox, stacked ? 2 : 1);
        SearchBox.HorizontalAlignment = stacked
            ? HorizontalAlignment.Stretch : HorizontalAlignment.Left;
    }
}
