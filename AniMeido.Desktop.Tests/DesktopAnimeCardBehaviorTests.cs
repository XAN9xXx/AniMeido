using AniMeido.Contracts.Models;
using AniMeido.Plugin.Base.Views.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Xunit;

namespace AniMeido.Desktop.Tests;

[Collection(UnoHeadlessTestHost.CollectionName)]
public sealed class DesktopAnimeCardBehaviorTests
{
    [Theory]
    [InlineData(AnimeCardDisplayMode.Poster)]
    [InlineData(AnimeCardDisplayMode.Compact)]
    [InlineData(AnimeCardDisplayMode.Detailed)]
    public Task ParentWrapperDoesNotReplaceTheCardsAnimeOrCaption(AnimeCardDisplayMode mode)
        => UnoHeadlessTestHost.RunAsync(async root =>
        {
            var anime = Example(1, "桐山零和3月的狮子");
            var wrapper = new Grid { DataContext = new { Title = "外层行数据" } };
            var card = new AnimeCard { Anime = anime, DisplayMode = mode, CardWidth = 150, Width = mode == AnimeCardDisplayMode.Detailed ? 520 : 150 };
            wrapper.Children.Add(card);
            root.Children.Add(wrapper);
            await Task.Delay(30);
            root.UpdateLayout();

            Assert.Same(anime, card.Anime);
            Assert.Equal(anime.Title, Text(card, "TitleText").Text);
            Assert.Equal(anime.Title, Text(card, "DetailedTitle").Text);
            Assert.Equal(anime.Description, Text(card, "DescriptionText").Text);
            var caption = Text(card, mode == AnimeCardDisplayMode.Detailed ? "DetailedTitle" : "TitleText");
            Assert.True(caption.ActualWidth > 0);
            Assert.True(caption.ActualHeight > 0);
            Assert.True(((SolidColorBrush)caption.Foreground).Color.A > 0);

            wrapper.DataContext = new { Title = "另一个外层对象" };
            Assert.Equal(anime.Title, caption.Text);
        });

    [Fact]
    public Task RecyclingUpdatesEveryTextFieldAndClearsTheRemovedAnime()
        => UnoHeadlessTestHost.RunAsync(async root =>
        {
            var first = Example(1, "第一部");
            var second = Example(2, "第二部") with { Description = "新的简介", Score = 6.2, AirDate = new DateOnly(2026, 2, 3) };
            var card = new AnimeCard { Anime = first, DisplayMode = AnimeCardDisplayMode.Detailed, Width = 520 };
            root.Children.Add(card);
            await Task.Delay(30);
            card.Anime = second;
            Assert.Equal(second.Title, Text(card, "TitleText").Text);
            Assert.Equal(second.Title, Text(card, "DetailedTitle").Text);
            Assert.Equal(second.Description, Text(card, "DescriptionText").Text);
            Assert.Equal("6.2", Text(card, "DetailedScore").Text);
            Assert.Contains(second.AirDate.Value.ToString(), Text(card, "DetailedMetadata").Text);

            card.Anime = null;
            Assert.Empty(Text(card, "TitleText").Text);
            Assert.Empty(Text(card, "DescriptionText").Text);
            Assert.Empty(Text(card, "DetailedScore").Text);
            Assert.False(((ProgressRing)card.FindName("RetryRing")).IsActive);
        });

    [Fact]
    public Task PendingTrackingKeepsButtonsPresentAndRestoresTheirOpacity()
        => UnoHeadlessTestHost.RunAsync(async root =>
        {
            var card = new AnimeCard { Anime = Example(1, "番剧"), ShowQuickActions = true };
            root.Children.Add(card);
            await Task.Delay(30);
            var watch = (Button)card.FindName("WatchActionButton");
            var follow = (Button)card.FindName("FollowActionButton");
            card.IsTrackingActionPending = true;
            Assert.True(watch.IsEnabled && follow.IsEnabled);
            Assert.Equal(.55, watch.Opacity);
            Assert.Equal(.55, follow.Opacity);
            card.IsTrackingActionPending = false;
            Assert.Equal(1, watch.Opacity);
            Assert.Equal(1, follow.Opacity);
        });

    private static TextBlock Text(AnimeCard card, string name) => (TextBlock)card.FindName(name);
    private static Anime Example(int id, string title) => new(id, title, null, [], new DateOnly(2026, 1, 1), null,
        "用于检查卡片文字的合成简介。", 2026, 1, Score: 8.7);
}
