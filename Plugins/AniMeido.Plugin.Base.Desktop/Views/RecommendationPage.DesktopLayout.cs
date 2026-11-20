using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using AniMeido.Plugin.Base.Models;
using AniMeido.Plugin.Base.ViewModels;
using AniMeido.Plugin.Base.Views.Controls;

namespace AniMeido.Plugin.Base.Views;

public sealed partial class RecommendationPage
{
    private bool? _desktopRecommendationStacked;

    private void OnDesktopRecommendationSizeChanged(object sender, SizeChangedEventArgs args)
    {
        if (args.NewSize.Width <= 0) return;
        var coverWidth = args.NewSize.Width < 750 ? 110 : args.NewSize.Width < 1000 ? 155 : 210;
        DesktopRecommendationFeature.ColumnDefinitions[1].Width = new GridLength(coverWidth);
        DesktopRecommendationFeature.ColumnSpacing = args.NewSize.Width < 750 ? 20 : args.NewSize.Width < 1000 ? 28 : 50;
        DesktopRecommendationCover.Width = coverWidth;
        DesktopRecommendationCover.Height = coverWidth * 1.5;
        var stacked = args.NewSize.Width < 900;
        if (_desktopRecommendationStacked == stacked) return;
        _desktopRecommendationStacked = stacked;
        Grid.SetRow(DesktopRecommendationSnapshot, stacked ? 1 : 0);
        Grid.SetColumn(DesktopRecommendationSnapshot, stacked ? 0 : 1);
        Grid.SetColumnSpan(DesktopRecommendationSnapshot, stacked ? 2 : 1);
        Grid.SetRow(DesktopColdStartChoices, stacked ? 1 : 0);
        Grid.SetColumn(DesktopColdStartChoices, stacked ? 0 : 1);
        Grid.SetColumnSpan(DesktopColdStartChoices, stacked ? 2 : 1);
    }

    private void UpdateDesktopRecommendationNavigation()
    {
        var index = ViewModel.SelectedItem is { } selected ? ViewModel.Items.IndexOf(selected) : -1;
        DesktopRecommendationPager.Visibility = index >= 0 ? Visibility.Visible : Visibility.Collapsed;
        DesktopPreviousRecommendationButton.IsEnabled = index > 0;
        DesktopNextRecommendationButton.IsEnabled = index >= 0 && index + 1 < ViewModel.Items.Count;
        DesktopRecommendationPageText.Text = index < 0 ? string.Empty : $"{index + 1} / {ViewModel.Items.Count}";
    }

    private void OnDesktopPreviousRecommendationClick(object sender, RoutedEventArgs args)
        => SelectDesktopRecommendation(-1);

    private void OnDesktopNextRecommendationClick(object sender, RoutedEventArgs args)
        => SelectDesktopRecommendation(1);

    private void SelectDesktopRecommendation(int direction)
    {
        if (ViewModel.SelectedItem is not { } selected) return;
        var index = ViewModel.Items.IndexOf(selected) + direction;
        if (index < 0 || index >= ViewModel.Items.Count) return;
        ViewModel.SelectedItem = ViewModel.Items[index];
        CandidateScroll.ChangeView(Math.Max(0, index * 116 - CandidateScroll.ViewportWidth / 2), null, null, true);
    }

    private void OnDesktopTagPreferencesCollapsed(Expander sender, ExpanderCollapsedEventArgs args)
        => CloseTagStackImmediately();

    private async void OnDesktopTagPreferenceChanged(object sender, SelectionChangedEventArgs args)
    {
        if (sender is not DesktopChoice { Tag: RecommendationTagPreference tag } choice)
            return;
        var requested = choice.SelectedIndex;
        var saved = DesktopRecommendationPreference.SelectionIndex(tag.IsLiked, tag.IsReduced);
        if (requested < 0 || requested == saved)
            return;

        choice.SelectedIndex = saved;
        if (!tag.CanEdit)
            return;
        try
        {
            await SaveTagAsync(choice, requested switch
            {
                0 => RecommendationAdjustment.Like,
                2 => RecommendationAdjustment.Reduce,
                _ => null,
            });
        }
        finally
        {
            if (ReferenceEquals(choice.Tag, tag))
                choice.SelectedIndex = DesktopRecommendationPreference.SelectionIndex(tag.IsLiked, tag.IsReduced);
        }
    }
}
