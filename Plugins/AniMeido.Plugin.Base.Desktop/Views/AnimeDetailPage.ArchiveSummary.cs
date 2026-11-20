using System.Globalization;
using AniMeido.Plugin.Base.ViewModels;
using Microsoft.Extensions.Logging;

namespace AniMeido.Plugin.Base.Views;

public sealed partial class AnimeDetailPage
{
    private void OnDesktopDetailSizeChanged(object sender, Microsoft.UI.Xaml.SizeChangedEventArgs args)
    {
        var availableWidth = Math.Max(0,
            args.NewSize.Width - DetailLayout.Padding.Left - DetailLayout.Padding.Right);
        RatingsSection.Width = Math.Min(780, availableWidth);
        var wide = args.NewSize.Width >= 900;
        DetailCoverColumn.Width = new(wide ? 220 : 0);
        DetailLayout.ColumnSpacing = wide ? 40 : 0;
        Microsoft.UI.Xaml.Controls.Grid.SetColumn(CoverSection, wide ? 1 : 0);
        Microsoft.UI.Xaml.Controls.Grid.SetRowSpan(CoverSection, wide ? 3 : 1);
        CoverSection.Width = wide ? 220 : 144;
        CoverSection.MaxHeight = wide ? 330 : 210;
        Microsoft.UI.Xaml.Controls.Grid.SetRow(DetailHeader, wide ? 0 : 1);
        Microsoft.UI.Xaml.Controls.Grid.SetRow(SynopsisSection, wide ? 1 : 2);
        Microsoft.UI.Xaml.Controls.Grid.SetRow(ActionSection, wide ? 2 : 3);
        Microsoft.UI.Xaml.Controls.Grid.SetRow(ArchiveSection, wide ? 3 : 4);
        Microsoft.UI.Xaml.Controls.Grid.SetRow(TagsSection, wide ? 4 : 5);
        Microsoft.UI.Xaml.Controls.Grid.SetRow(RatingsSection, wide ? 5 : 6);
        Microsoft.UI.Xaml.Controls.Grid.SetRow(FactsSection, wide ? 6 : 7);
        Microsoft.UI.Xaml.Controls.Grid.SetRow(CharacterSection, wide ? 7 : 8);
    }

    private DesktopPersonalArchiveSummaryReader _desktopArchiveSummary = null!;
    private CancellationTokenSource? _desktopArchiveSummaryCancellation;

    private void InitializeDesktopArchiveSummary()
        => _desktopArchiveSummary = new(
            async (id, token) => (await _archive.GetArchiveAsync(id, token))?.PersonalRating,
            _archive.GetAnimeTagsAsync);

    private void CancelDesktopArchiveSummary()
        => _desktopArchiveSummaryCancellation?.Cancel();

    private void ResetDesktopArchiveSummary()
    {
        CancelDesktopArchiveSummary();
        DesktopPersonalArchiveSummaryText.Text = "我的评分 未评分    个人标签 未设置";
    }

    private async Task RefreshDesktopArchiveSummaryAsync(
        int animeId, int version, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested
            || !IsCurrentNavigation(version, animeId))
            return;
        using var current = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var previous = _desktopArchiveSummaryCancellation;
        _desktopArchiveSummaryCancellation = current;
        previous?.Cancel();
        bool IsCurrent() => !current.IsCancellationRequested
            && IsCurrentNavigation(version, animeId);
        try
        {
            var summary = await _desktopArchiveSummary.ReadAsync(
                animeId, IsCurrent, current.Token);
            if (summary is null || !IsCurrent()) return;
            var rating = summary.Rating?.ToString("0.0", CultureInfo.CurrentCulture) ?? "未评分";
            var tags = summary.Tags.Count == 0 ? "未设置" : string.Join("、", summary.Tags);
            DesktopPersonalArchiveSummaryText.Text = $"我的评分 {rating}    个人标签 {tags}";
        }
        catch (OperationCanceledException) when (current.IsCancellationRequested)
        {
        }
#pragma warning disable CA1031 // 摘要读取降级由页面统一报告，保留主详情与编辑入口。
        catch (Exception error)
        {
            if (!IsCurrent()) return;
            _logger.LogWarning(error, "Personal archive summary unavailable for {AnimeId}", animeId);
            DesktopPersonalArchiveSummaryText.Text = "个人档案暂时无法读取";
        }
#pragma warning restore CA1031
        finally
        {
            if (ReferenceEquals(_desktopArchiveSummaryCancellation, current))
                _desktopArchiveSummaryCancellation = null;
        }
    }
}
