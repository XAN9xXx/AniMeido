using AniMeido.Contracts;
using AniMeido.Contracts.Models;
using AniMeido.Plugin.Base.Services;
using AniMeido.Plugin.Base.Models;
using AniMeido.Plugin.Base.ViewModels;
using AniMeido.Plugin.Base.Views;
using AniMeido.Plugin.Base.Views.Controls;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Xunit;
using Xunit.Abstractions;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using SkiaSharp;
using System.Reflection;
using System.Diagnostics;
using Windows.Foundation;
using Windows.UI.Input.Preview.Injection;
using static AniMeido.Desktop.Tests.DesktopTodayRecommendationPageTests;

namespace AniMeido.Desktop.Tests;

[Collection(UnoHeadlessTestHost.CollectionName)]
public sealed class DesktopCatalogTemplateTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(960, false)]
    [InlineData(660, true)]
    public Task SeasonFormatButtonsKeepTheirPositionsAndSelectionWhileCountsChange(int width, bool selectOva)
        => UnoHeadlessTestHost.RunAsync(async root =>
        {
            await using var fixture = await Fixture.CreateAsync();
            var store = new AniMeido.App.Services.DesktopSettingsStore(fixture);
            await store.SaveAsync(new AniMeido.App.Services.DesktopSettings { VisualStyle = "Paper", ThemeSetting = "Dark", FontStyle = "Serif", CatalogCardStyle = selectOva ? "Cover" : "Detailed" });
            await using var theme = new AniMeido.App.Services.ThemeService(store);
            await theme.InitializeThemeAsync(root);
            var winter = new TaskCompletionSource<List<Anime>>(TaskCreationOptions.RunContinuationsAsynchronously);
            static List<Anime> Items(int total, int ova, int tv, int ona, int movie, int year, int month)
                => Enumerable.Range(0, total).Select(i => Item(month * 10_000 + i, $"合成季度作品 {i:D3}") with
                {
                    AirDate = new DateOnly(year, month, 1),
                    MediaFormat = i < ova ? AnimeMediaFormat.Ova
                        : i < ova + tv ? AnimeMediaFormat.Television
                        : i < ova + tv + ona ? AnimeMediaFormat.Ona
                        : i < ova + tv + ona + movie ? AnimeMediaFormat.Movie : AnimeMediaFormat.Unknown,
                }).ToList();
            var source = new SeasonSource((year, season, _) => season == Season.Spring
                ? Task.FromResult(Items(253, 17, 100, 9, 2, year, 4)) : winter.Task);
            var tracking = new TrackingService(fixture.Factory);
            var page = new PastSeasonPage(source, new CacheService(fixture.Factory),
                new DragDropService(tracking, new PersonalFeatureStateStore(fixture.Factory), NullLogger<DragDropService>.Instance),
                tracking, fixture.Navigation, NullLogger<PastSeasonPage>.Instance) { Width = width, Height = 720 };
            await page.OnNavigatedToAsync(new PastSeasonTarget(2025, Season.Spring));
            try
            {
                await AttachAsync(root, page);
                await WaitAsync(root, () => Chips().Length == 6 && page.ViewModel.Entries.Count == 253);
                var mouse = new DesktopTestMouse(root);
                var original = Chips();
                var ova = original.Single(chip => Format(chip) == AnimeMediaFormat.Ova);
                var shelf = DesktopAcceptanceInputTests.Descendants(page).OfType<DesktopWheelShelf>()
                    .Single(viewer => DesktopAcceptanceInputTests.Descendants(viewer).Contains(ova));
                if (selectOva)
                {
                    await WaitAsync(root, () => shelf.ScrollableWidth > 0);
                    shelf.ChangeView(shelf.ScrollableWidth, null, null, true);
                    await WaitAsync(root, () => shelf.HorizontalOffset > 0);
                    mouse.Click(ova);
                    await WaitAsync(root, () => page.ViewModel.SelectedFormat == AnimeMediaFormat.Ova && page.ViewModel.Entries.Count == 17);
                    Assert.Equal(original, Chips());
                }
                var selected = selectOva ? ova : original[0];
                var movie = original.Single(chip => Format(chip) == AnimeMediaFormat.Movie);
                Assert.True(movie.IsEnabled);
                var positions = original.Select(chip => (X: chip.TransformToVisual(shelf).TransformPoint(default).X, Width: chip.ActualWidth)).ToArray();
                var shelfOffset = shelf.HorizontalOffset;
                var name = width == 960 ? "wide" : "narrow";
                var prev = Assert.IsType<Button>(page.FindName("PrevSeasonButton"));
                mouse.MoveTo(prev);
                await UnoVisualEvidence.CaptureAsync(page, "E-acceptance", $"season-chips-{name}-00-spring");
                var rows = (ItemsRepeater)page.FindName("CatalogRows");
                var heading = (TextBlock)page.FindName("SeasonSummary");
                await WaitAsync(root, () => VisibleCards(rows).Length > 0);
                var springHeading = heading.Text;
                var oldSource = rows.ItemsSource;
                mouse.Click(prev);
                for (var frame = 0; frame < 22; frame++)
                {
                    root.UpdateLayout();
                    Assert.Same(oldSource, rows.ItemsSource);
                    Assert.Equal(springHeading, heading.Text);
                    Assert.NotEmpty(VisibleCards(rows));
                    Assert.All(VisibleCards(rows), card => Assert.Equal(4, card.Anime!.AirDate!.Value.Month));
                    Assert.Equal(Visibility.Collapsed, ((ItemsRepeater)page.FindName("SkeletonRepeater")).Visibility);
                    await Task.Delay(16);
                }
                await WaitAsync(root, () => page.ViewModel.IsLoading);
                await UnoVisualEvidence.CaptureAsync(page, "E-acceptance", $"season-chips-{name}-01-loading");
                AssertStable("spring", "17");

                winter.SetResult(Items(249, 9, 99, 10, 0, 2025, 1));
                for (var frame = 0; frame < 8; frame++)
                {
                    root.UpdateLayout();
                    var cards = VisibleCards(rows);
                    Assert.NotEmpty(cards);
                    int expectedMonth = heading.Text.Contains("冬", StringComparison.Ordinal) ? 1 : 4;
                    Assert.All(cards, card => Assert.Equal(expectedMonth, card.Anime!.AirDate!.Value.Month));
                    await Task.Delay(16);
                }
                await WaitAsync(root, () => !page.ViewModel.IsLoading && page.ViewModel.LoadedAnime.Count == 249);
                await UnoVisualEvidence.CaptureAsync(page, "E-acceptance", $"season-chips-{name}-02-winter");
                await WaitAsync(root, () => VisibleCards(rows).Length > 0 &&
                    VisibleCards(rows).All(card => card.Anime?.AirDate?.Month == 1));
                Assert.Contains("冬", heading.Text);
                AssertStable("winter", "9");
                Assert.False(movie.IsEnabled);
                Assert.Equal("0", Count(movie));
                Assert.Equal(selectOva ? 9 : 249, page.ViewModel.Entries.Count);

                void AssertStable(string stage, string ovaCount)
                {
                    var actual = Chips();
                    output.WriteLine($"{name}/{stage}: {string.Join(" | ", actual.Select(c => $"{Format(c)}:{Count(c)},selected={c.IsChecked},x={c.TransformToVisual(shelf).TransformPoint(default).X:0.#},width={c.ActualWidth:0.#}"))}");
                    Assert.Equal(original, actual);
                    Assert.Equal(selected, Assert.Single(actual, chip => chip.IsChecked == true));
                    Assert.Equal(ovaCount, Count(ova));
                    Assert.InRange(Math.Abs(shelfOffset - shelf.HorizontalOffset), 0, .5);
                    for (var i = 0; i < original.Length; i++)
                    {
                        Assert.InRange(Math.Abs(positions[i].X - original[i].TransformToVisual(shelf).TransformPoint(default).X), 0, .5);
                        Assert.InRange(Math.Abs(positions[i].Width - original[i].ActualWidth), 0, .5);
                    }
                }

                ToggleButton[] Chips() => DesktopAcceptanceInputTests.Descendants(page).OfType<ToggleButton>()
                    .Where(chip => chip.Tag is PastSeasonFormatChip || chip.Name.StartsWith("FormatChip", StringComparison.Ordinal)).ToArray();
                static AnimeMediaFormat? Format(ToggleButton chip) => chip.Tag switch
                {
                    PastSeasonFormatChip item => item.Format,
                    AnimeMediaFormat format => format,
                    _ => null,
                };
                static string Count(ToggleButton chip) => DesktopAcceptanceInputTests.Descendants(chip).OfType<TextBlock>()
                    .First(text => int.TryParse(text.Text, out _)).Text;
            }
            finally { await DetachAsync(root, page); }
        });

    [Fact]
    public Task CatalogFiltersKeepScrollUntilResultsChange()
        => UnoHeadlessTestHost.RunAsync(async root =>
        {
            await using var fixture = await Fixture.CreateAsync();
            var source = new SeasonSource((_, _, _) => Task.FromResult(Enumerable.Range(1, 100)
                .Select(i => Item(i, $"作品{(i % 2 == 0 ? "甲" : "乙")}{i}")).ToList()));
            var tracking = new TrackingService(fixture.Factory);
            var page = new PastSeasonPage(source, new CacheService(fixture.Factory),
                new DragDropService(tracking, new PersonalFeatureStateStore(fixture.Factory), NullLogger<DragDropService>.Instance),
                tracking, fixture.Navigation, NullLogger<PastSeasonPage>.Instance) { Width = 800, Height = 650 };
            try
            {
                await AttachAsync(root, page);
                var viewer = (ScrollViewer)page.FindName("CatalogScroller");
                var search = (AutoSuggestBox)page.FindName("FilterBox");
                await WaitAsync(root, () => page.ViewModel.Entries.Count == 100 && viewer.ScrollableHeight > 1600);
                viewer.ChangeView(null, 1200, null, true);
                await SettleLayoutAsync();
                var offset = viewer.VerticalOffset;
                Assert.True(offset > 1000);

                ((ToggleSwitch)page.FindName("HideCompletedSwitch")).IsOn = true;
                await SettleLayoutAsync();
                Assert.True(page.ViewModel.HideCompleted);
                Assert.InRange(Math.Abs(viewer.VerticalOffset - offset), 0, .5);

                search.Text = "作品";
                await SettleLayoutAsync();
                Assert.Equal("作品", page.ViewModel.Query);
                Assert.InRange(Math.Abs(viewer.VerticalOffset - offset), 0, .5);

                search.Text = "甲";
                await WaitAsync(root, () => page.ViewModel.Entries.Count == 50 && viewer.VerticalOffset < .5);
                Assert.True(viewer.ScrollableHeight > 1200);

                async Task SettleLayoutAsync()
                {
                    for (var i = 0; i < 6; i++) { root.UpdateLayout(); await Task.Delay(16); }
                }
            }
            finally { await DetachAsync(root, page); }
        });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task ReturningToTheSamePosterSeasonKeepsTheFirstVisibleWorkAndItsRelativeTop(bool mixedTitles)
        => UnoHeadlessTestHost.RunAsync(async root =>
        {
            await using var fixture = await Fixture.CreateAsync();
            var source = new SeasonSource((year, _, _) => Task.FromResult(
                Enumerable.Range(0, 180).Select(i => Item(34_000 + i, $"合成作品 {i:D3}"
                    + (mixedTitles && i % 3 == 0 ? " 有一个需要换行显示的较长标题" : "")) with
                { AirDate = new DateOnly(year, 4, 1), MediaFormat = AnimeMediaFormat.Television }).ToList()));
            var tracking = new TrackingService(fixture.Factory);
            var previous = DesktopPresentationPreferences.UseDetailedCatalogCards;
            DesktopPresentationPreferences.Apply(false, DesktopPresentationPreferences.ShowPlanReminders);
            var page = new PastSeasonPage(source, new CacheService(fixture.Factory),
                new DragDropService(tracking, new PersonalFeatureStateStore(fixture.Factory), NullLogger<DragDropService>.Instance),
                tracking, fixture.Navigation, NullLogger<PastSeasonPage>.Instance);
            var frame = new Frame { Content = page, Width = 960, Height = 720 };
            await page.OnNavigatedToAsync(new PastSeasonTarget(2025, Season.Spring));
            root.Children.Add(frame);
            try
            {
                var viewer = (ScrollViewer)page.FindName("CatalogScroller");
                var rows = (ItemsRepeater)page.FindName("CatalogRows");
                await WaitAsync(root, () => page.ViewModel.Entries.Count == 180 && viewer.ScrollableHeight > 1000);
                frame.Width = 640;
                await SettleAsync();
                frame.Width = 960;
                ((AutoSuggestBox)page.FindName("FilterBox")).Text = "作品";
                ((ComboBox)page.FindName("SortComboBox")).SelectedIndex = 2;
                await SettleAsync();
                viewer.ChangeView(null, 800, null, true);
                await SettleAsync();
                var expected = Anchor();
                var offset = viewer.VerticalOffset;
                Assert.True(offset > 700);
                for (var visit = 0; visit < 3; visit++)
                {
                    frame.Content = new Page { Content = new TextBlock { Text = "作品详情" } };
                    await WaitAsync(root, () => !page.IsLoaded);
                    frame.Content = page;
                    await WaitAsync(root, () => page.IsLoaded && VisibleCards(rows).Length > 0);
                    await SettleAsync();
                    var actual = Anchor();
                    output.WriteLine($"poster-return={visit}; expected={expected}; actual={actual}; offset={offset}->{viewer.VerticalOffset}");
                    Assert.Equal(expected.Id, actual.Id);
                    Assert.InRange(Math.Abs(expected.Y - actual.Y), 0, 0.5);
                    Assert.InRange(Math.Abs(offset - viewer.VerticalOffset), 0, 0.5);
                }

                frame.Content = new Page();
                await WaitAsync(root, () => !page.IsLoaded);
                frame.Content = page;
                await WaitAsync(root, () => page.IsLoaded && VisibleCards(rows).Length > 0);
                var point = viewer.TransformToVisual(root).TransformPoint(new Point(160, 90));
                var mouse = Assert.IsType<InputInjector>(InputInjector.TryCreate(root));
                var trace = new List<double>();
                viewer.ViewChanged += (_, _) => trace.Add(viewer.VerticalOffset);
                var received = new List<(int Delta, bool Horizontal)>();
                viewer.AddHandler(UIElement.PointerWheelChangedEvent, new PointerEventHandler((_, args) =>
                {
                    var properties = args.GetCurrentPoint(viewer).Properties;
                    received.Add((properties.MouseWheelDelta, properties.IsHorizontalMouseWheel));
                }), handledEventsToo: true);
                mouse.InjectMouseInput([
                    new() { MouseOptions = InjectedInputMouseOptions.Move, DeltaX = (int)point.X, DeltaY = (int)point.Y },
                ]);
                await RollAsync(-120);
                Assert.True(viewer.VerticalOffset > offset + 15, "返回番剧库后向下滚动不应被拉回。");
                await RollAsync(120);

                async Task RollAsync(int delta)
                {
                    var before = viewer.VerticalOffset;
                    received.Clear();
                    trace.Clear();
                    trace.Add(before);
                    // Uno 7.0 的离屏注入器用 DeltaY 传轮值；在实际滚动容器验证收到的方向和幅度。
                    mouse.InjectMouseInput([
                        new() { MouseOptions = InjectedInputMouseOptions.Wheel, DeltaY = delta, TimeOffsetInMilliseconds = 1 },
                    ]);
                    await SettleAsync();
                    await SettleAsync();
                    Assert.Equal((delta, false), Assert.Single(received));
                    var direction = -Math.Sign(delta);
                    output.WriteLine($"wheel={delta}; offset={before:0.##}->{viewer.VerticalOffset:0.##}; updates={trace.Count - 1}");
                    Assert.True((viewer.VerticalOffset - before) * direction > 15, "滚轮一格应移动到输入方向，不能只短暂跳动。");
                    Assert.All(trace.Zip(trace.Skip(1)), pair =>
                        Assert.True((pair.Second - pair.First) * direction >= -.5, $"同一格滚轮滚动时偏移反向：{pair.First:0.##} → {pair.Second:0.##}。"));
                }

                (int Id, double Y) Anchor() => VisibleCards(rows)
                    .Select(card => (Card: card, Location: card.TransformToVisual(viewer).TransformPoint(default)))
                    .Where(item => item.Location.Y + item.Card.ActualHeight > 0 && item.Location.Y < viewer.ViewportHeight)
                    .OrderBy(item => item.Location.Y).ThenBy(item => item.Location.X)
                    .Select(item => (item.Card.Anime!.ID, item.Location.Y)).First();

                async Task SettleAsync()
                {
                    for (var tick = 0; tick < 12; tick++)
                    {
                        root.UpdateLayout();
                        await Task.Delay(16);
                    }
                }
            }
            finally
            {
                root.Children.Remove(frame);
                DesktopPresentationPreferences.Apply(previous, DesktopPresentationPreferences.ShowPlanReminders);
            }
        });

    [Fact]
    [Trait("Category", "Mechanism")]
    public Task ScrollingSixHundredPostersKeepsOnlyANearbyBandOfRealizedCards()
        => UnoHeadlessTestHost.RunAsync(async root =>
        {
            await using var fixture = await Fixture.CreateAsync();
            var source = new SeasonSource((year, _, _) => Task.FromResult(
                Enumerable.Range(0, 600).Select(i => Item(30_000 + i, $"合成番剧 {i:D3}") with
                { AirDate = new DateOnly(year, 4, 1), MediaFormat = AnimeMediaFormat.Television }).ToList()));
            var tracking = new TrackingService(fixture.Factory);
            var previous = DesktopPresentationPreferences.UseDetailedCatalogCards;
            DesktopPresentationPreferences.Apply(false, DesktopPresentationPreferences.ShowPlanReminders);
            var page = new PastSeasonPage(source, new CacheService(fixture.Factory),
                new DragDropService(tracking, new PersonalFeatureStateStore(fixture.Factory), NullLogger<DragDropService>.Instance),
                tracking, fixture.Navigation, NullLogger<PastSeasonPage>.Instance) { Width = 960, Height = 720 };
            await page.OnNavigatedToAsync(new PastSeasonTarget(2025, Season.Spring));
            try
            {
                await AttachAsync(root, page);
                var rows = (ItemsRepeater)page.FindName("CatalogRows");
                var viewer = (ScrollViewer)page.FindName("CatalogScroller");
                await WaitAsync(root, () => page.ViewModel.Entries.Count == 600 && VisibleCards(rows).Length > 0);
                await Task.Delay(420);
                output.WriteLine($"poster-scroll initial={VisibleCards(rows).Length}; viewport={viewer.ViewportWidth:F0}x{viewer.ViewportHeight:F0}; extent={viewer.ExtentHeight:F0}; scrollable={viewer.ScrollableHeight:F0}");
                var workTimes = new List<double>();
                var peak = 0;
                using var work = new DesktopManagedImageLifetimeTests.ImageWorkSample();
                for (var i = 0; i < 30; i++)
                {
                    var start = Stopwatch.GetTimestamp();
                    viewer.ChangeView(null, i * 108, null, disableAnimation: true);
                    root.UpdateLayout();
                    workTimes.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
                    await Task.Delay(16);
                    peak = Math.Max(peak, VisibleCards(rows).Length);
                }
                workTimes.Sort();
                output.WriteLine($"poster-scroll cache={rows.VerticalCacheLength}; peakRealized={peak}; offset={viewer.VerticalOffset:F0}; p50LayoutMs={workTimes[14]:F2}; p95LayoutMs={workTimes[28]:F2}; {work}");
                Assert.InRange(peak, 8, 32);
                Assert.True(viewer.VerticalOffset > 2700);
            }
            finally
            {
                await DetachAsync(root, page);
                DesktopPresentationPreferences.Apply(previous, DesktopPresentationPreferences.ShowPlanReminders);
            }
        });

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [InlineData(true, false)]
    public Task ActualFormatFilterRemovesOnePosterAndKeepsTheSurvivingCoversWhenTheyMove(bool crossRow, bool motion)
        => UnoHeadlessTestHost.RunAsync(async root =>
        {
            await using var fixture = await Fixture.CreateAsync();
            var order = Enumerable.Range(0, 6).Select(_ => new DesktopManagedImageLifetimeTests.CachedImage(false, 360, 540)).ToArray();
            var removedIndex = crossRow ? 1 : 3;
            var colors = new[] { "#596788", "#396F78", "#8D5262", "#76512F", "#4C7561", "#674E79" };
            for (var i = 0; i < order.Length; i++)
            {
                using var bitmap = new SKBitmap(360, 540);
                bitmap.Erase(SKColor.Parse(colors[i]));
                using var canvas = new SKCanvas(bitmap);
                using var ink = new SKPaint { Color = SKColor.Parse("#E6C686"), IsAntialias = true };
                canvas.DrawCircle(105 + i * 27, 180, 40, ink);
                using var bytes = bitmap.Encode(SKEncodedImageFormat.Jpeg, 92);
                using var file = File.Create(order[i].Path);
                bytes.SaveTo(file);
            }
            var calls = 0;
            var source = new SeasonSource((year, _, _) =>
            {
                calls++;
                return Task.FromResult(order.Select((cover, index) => Item(cover.Id, $"春季短篇第 {index + 1} 部") with
                {
                    AirDate = new DateOnly(year, 4, 1 + index), CoverURL = cover.Url,
                    MediaFormat = index == removedIndex ? AnimeMediaFormat.Movie : AnimeMediaFormat.Television,
                }).ToList());
            });
            var tracking = new TrackingService(fixture.Factory);
            var page = new PastSeasonPage(source, new CacheService(fixture.Factory),
                new DragDropService(tracking, new PersonalFeatureStateStore(fixture.Factory), NullLogger<DragDropService>.Instance),
                tracking, fixture.Navigation, NullLogger<PastSeasonPage>.Instance) { Width = 780, Height = 1010 };
            var previous = DesktopPresentationPreferences.UseDetailedCatalogCards;
            var resources = Application.Current.Resources;
            EventHandler<object>? observeLayout = null;
            var previousMotion = resources["DesktopMotionEnabled"];
            resources["DesktopMotionEnabled"] = motion;
            DesktopPresentationPreferences.Apply(false, DesktopPresentationPreferences.ShowPlanReminders);
            await page.OnNavigatedToAsync(new PastSeasonTarget(2025, Season.Spring));
            try
            {
                await AttachAsync(root, page);
                var rows = (ItemsRepeater)page.FindName("CatalogRows");
                await WaitAsync(root, () => VisibleCards(rows).Length == 6
                    && VisibleCards(rows).All(card => Cover(card) is BitmapSource { PixelWidth: > 0 }));
                Assert.Equal(2, ((System.Collections.ICollection)rows.ItemsSource).Count);
                var cards = VisibleCards(rows).ToDictionary(card => card.Anime!.ID, card => (Card: card, Source: Cover(card)));
                var destination = order[crossRow ? 3 : 4].Id;
                var before = cards[destination].Card.TransformToVisual(rows).TransformPoint(default);
                var chip = Find<ToggleButton>(page, button => button.Tag is PastSeasonFormatChip { Format: AnimeMediaFormat.Television });
                var sequence = !motion ? "catalog-reduced" : crossRow ? "catalog-cross-row" : "catalog-format";
                var sampled = new List<double>();
                var layoutAndFrames = new List<double>();
                await UnoVisualEvidence.CaptureAsync(page, "C-motion", $"{sequence}-00-six-before");
                // 记录每次新布局的位置，避免只采到动画中段，漏掉先到终点再往回跳的一帧。
                observeLayout = (_, _) =>
                {
                    var visible = VisibleCards(rows);
                    if (visible.Length == 5 && visible.SingleOrDefault(c => c.Anime?.ID == destination) is { } card)
                    {
                        var point = card.TransformToVisual(rows).TransformPoint(default);
                        layoutAndFrames.Add(crossRow ? point.Y : point.X);
                    }
                };
                page.LayoutUpdated += observeLayout;
                typeof(ButtonBase).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(chip, null);
                Assert.True(chip.IsChecked);
                for (var i = 0; i < 4; i++)
                {
                    if (i > 0) await Task.Delay(i == 3 ? 90 : 16);
                    root.UpdateLayout();
                    var visible = VisibleCards(rows);
                    if (visible.SingleOrDefault(c => c.Anime?.ID == destination) is { } moving)
                    {
                        var pos = moving.TransformToVisual(rows).TransformPoint(default);
                        sampled.Add(crossRow ? pos.Y : pos.X);
                        layoutAndFrames.Add(sampled[^1]);
                    }
                    Assert.InRange(visible.Length, 5, 6);
                    Assert.Equal(visible.Length, visible.Select(card => card.Anime!.ID).Distinct().Count());
                    Assert.False(page.ViewModel.IsLoading);
                    Assert.Equal(Visibility.Collapsed, ((ItemsRepeater)page.FindName("SkeletonRepeater")).Visibility);
                    foreach (var card in visible.Where(card => card.Anime!.ID != order[removedIndex].Id))
                    {
                        var original = cards[card.Anime!.ID].Source;
                        if (crossRow && card.Anime.ID == destination)
                        {
                            var actual = Assert.IsType<BitmapImage>(Cover(card));
                            Assert.Equal(Assert.IsType<BitmapImage>(original).UriSource, actual.UriSource);
                            Assert.True(actual.UriSource.IsFile, "跨行封面直接读本地缓存。");
                        }
                        else Assert.Same(original, Cover(card));
                        Assert.Equal(card.Anime.Title, ((TextBlock)card.FindName("TitleText")).Text);
                    }
                    await UnoVisualEvidence.CaptureAsync(page, "C-motion", $"{sequence}-{i + 1:00}-reflow");
                }
                var final = VisibleCards(rows);
                Assert.Equal(5, final.Length);
                Assert.DoesNotContain(final, card => card.Anime!.ID == order[removedIndex].Id);
                await Task.Delay(220);
                var after = final.Single(card => card.Anime!.ID == destination).TransformToVisual(rows).TransformPoint(default);
                Assert.True(crossRow ? after.Y < before.Y : after.X < before.X);
                var start = crossRow ? before.Y : before.X;
                var end = crossRow ? after.Y : after.X;
                Assert.True(motion == sampled.Any(position => position > end + .1 && position < start - .1),
                    $"motion={motion}, start={start}, frames=[{string.Join(", ", sampled)}], end={end}");
                var positions = layoutAndFrames.Prepend(start).Append(end).ToArray();
                Assert.True(positions.Zip(positions.Skip(1)).All(pair => pair.Second <= pair.First + .1),
                    $"移动中途不能回跳：{string.Join(", ", positions)}");
                Assert.Equal(1, calls);
            }
            finally
            {
                page.LayoutUpdated -= observeLayout;
                await DetachAsync(root, page);
                resources["DesktopMotionEnabled"] = previousMotion;
                DesktopPresentationPreferences.Apply(previous, DesktopPresentationPreferences.ShowPlanReminders);
                foreach (var cover in order) cover.Dispose();
            }

            static ImageSource? Cover(AnimeCard card) => ((Image)card.FindName("CoverImage")).Source;
        });

    [Fact]
    public Task FilteringOutAMiddlePosterRowKeepsRealizedCardsOnBothSides()
        => UnoHeadlessTestHost.RunAsync(async root =>
        {
            await using var fixture = await Fixture.CreateAsync();
            var source = new SeasonSource((year, _, _) => Task.FromResult(Enumerable.Range(1, 9)
                .Select(i => Item(i, $"{i:D2} {(i is >= 4 and <= 6 ? "短篇" : "长篇")}") with
                { AirDate = new DateOnly(year, 4, i) }).ToList()));
            var tracking = new TrackingService(fixture.Factory);
            var page = new PastSeasonPage(source, new CacheService(fixture.Factory),
                new DragDropService(tracking, new PersonalFeatureStateStore(fixture.Factory), NullLogger<DragDropService>.Instance),
                tracking, fixture.Navigation, NullLogger<PastSeasonPage>.Instance) { Width = 780, Height = 1010 };
            var previous = DesktopPresentationPreferences.UseDetailedCatalogCards;
            DesktopPresentationPreferences.Apply(false, DesktopPresentationPreferences.ShowPlanReminders);
            try
            {
                await page.OnNavigatedToAsync(new PastSeasonTarget(2025, Season.Spring));
                await AttachAsync(root, page);
                var rows = (ItemsRepeater)page.FindName("CatalogRows");
                await WaitAsync(root, () => VisibleCards(rows).Length == 9);
                var before = VisibleCards(rows).ToDictionary(card => card.Anime!.ID);
                Assert.Equal(3, ((System.Collections.ICollection)rows.ItemsSource).Count);
                page.ViewModel.SetQuery("长篇");
                await WaitAsync(root, () => VisibleCards(rows).Length == 6);
                Assert.Equal(new[] { 1, 2, 3, 7, 8, 9 }, VisibleCards(rows).Select(card => card.Anime!.ID).Order());
                Assert.All(VisibleCards(rows), card => Assert.Same(before[card.Anime!.ID], card));
            }
            finally
            {
                await DetachAsync(root, page);
                DesktopPresentationPreferences.Apply(previous, DesktopPresentationPreferences.ShowPlanReminders);
            }
        });

    [Fact]
    public Task SwitchingCatalogLayoutReleasesRowsOfTheInactiveTemplate()
        => UnoHeadlessTestHost.RunAsync(async root =>
        {
            await using var fixture = await Fixture.CreateAsync();
            var source = new SeasonSource((year, _, _) => Task.FromResult(SeasonItems(year)));
            var tracking = new TrackingService(fixture.Factory);
            var page = new PastSeasonPage(source, new CacheService(fixture.Factory),
                new DragDropService(tracking, new PersonalFeatureStateStore(fixture.Factory), NullLogger<DragDropService>.Instance),
                tracking, fixture.Navigation, NullLogger<PastSeasonPage>.Instance);
            var previous = DesktopPresentationPreferences.UseDetailedCatalogCards;
            DesktopPresentationPreferences.Apply(false, DesktopPresentationPreferences.ShowPlanReminders);
            try
            {
                await page.OnNavigatedToAsync(new PastSeasonTarget(2025, Season.Spring));
                await AttachAsync(root, page);
                var repeater = (ItemsRepeater)page.FindName("CatalogRows");
                await WaitAsync(root, () => VisibleCards(repeater).Length == 2);
                var expected = VisibleCards(repeater).Select(c => c.Anime!.ID).Order().ToArray();
                foreach (var detailed in new[] { true, false })
                {
                    var oldRows = (System.Collections.ICollection)repeater.ItemsSource;
                    await DetachAsync(root, page);
                    DesktopPresentationPreferences.Apply(detailed, DesktopPresentationPreferences.ShowPlanReminders);
                    await AttachAsync(root, page);
                    await WaitAsync(root, () => repeater.ItemsSource is not null && !ReferenceEquals(oldRows, repeater.ItemsSource));
                    Assert.Empty(oldRows);
                    await WaitAsync(root, () => VisibleCards(repeater).Length == 2);
                    Assert.Equal(expected, VisibleCards(repeater).Select(c => c.Anime!.ID).Order());
                }
            }
            finally
            {
                await DetachAsync(root, page);
                DesktopPresentationPreferences.Apply(previous, DesktopPresentationPreferences.ShowPlanReminders);
            }
        });

    [Fact]
    public Task FilteringKeepsTheRowsWhoseContentsDidNotChange()
        => UnoHeadlessTestHost.RunAsync(async root =>
        {
            await using var fixture = await Fixture.CreateAsync();
            var source = new SeasonSource((year, _, _) => Task.FromResult(SeasonItems(year)));
            var tracking = new TrackingService(fixture.Factory);
            var page = new PastSeasonPage(source, new CacheService(fixture.Factory),
                new DragDropService(tracking, new PersonalFeatureStateStore(fixture.Factory), NullLogger<DragDropService>.Instance),
                tracking, fixture.Navigation, NullLogger<PastSeasonPage>.Instance);
            await page.OnNavigatedToAsync(new PastSeasonTarget(2025, Season.Spring));
            try
            {
                await AttachAsync(root, page);
                var rows = (ItemsRepeater)page.FindName("CatalogRows");
                await WaitAsync(root, () => VisibleCards(rows).Length == 2);
                var first = rows.TryGetElement(0);
                var children = VisibleCards(rows);
                var sourceList = rows.ItemsSource;
                await UnoVisualEvidence.CaptureAsync(page, "C-motion", "catalog-00-before-filter");
                page.ViewModel.SetHideCompleted(true);
                for (var i = 0; i < 4; i++)
                {
                    await Task.Delay(16); root.UpdateLayout();
                    Assert.Same(sourceList, rows.ItemsSource);
                    Assert.Same(first, rows.TryGetElement(0));
                    Assert.Equal(children, VisibleCards(rows));
                    await UnoVisualEvidence.CaptureAsync(page, "C-motion", $"catalog-{i + 1:00}-same-visible-rows");
                }
                page.ViewModel.SetQuery("这条搜索没有匹配的作品");
                await WaitAsync(root, () => page.ViewModel.Entries.Count == 0 && VisibleCards(rows).Length == 0);
                Assert.Same(sourceList, rows.ItemsSource);
                await UnoVisualEvidence.CaptureAsync(page, "C-motion", "catalog-05-empty-filter");
            }
            finally { await DetachAsync(root, page); }
        });

    [Theory]
    [InlineData(false, false, false)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    [InlineData(true, true, false)]
    [InlineData(true, false, true)]
    [InlineData(true, true, true)]
    [Trait("Category", "Mechanism")]
    public Task FlyoutYearAndSameQuarterClickReplaceTheActualCardsAndRejectAnOldReply(bool detailed, bool lateOldReply, bool singleNewEntry)
        => UnoHeadlessTestHost.RunAsync(async root =>
        {
            await using var fixture = await Fixture.CreateAsync();
            var previous = DesktopPresentationPreferences.UseDetailedCatalogCards;
            DesktopPresentationPreferences.Apply(detailed, DesktopPresentationPreferences.ShowPlanReminders);
            var requests = new List<PastSeasonTarget>();
            var oldReply = new TaskCompletionSource<List<Anime>>(TaskCreationOptions.RunContinuationsAsynchronously);
            var source = new SeasonSource((year, season, _) =>
            {
                requests.Add(new(year, season));
                return year == 2024 && lateOldReply ? oldReply.Task
                    : Task.FromResult(SeasonItems(year).Take(year == 2025 && singleNewEntry ? 1 : 2).ToList());
            });
            var tracking = new TrackingService(fixture.Factory);
            await tracking.SetStatusAsync(20231, AnimeTrackingStatus.Watching);
            await tracking.SetStatusAsync(20251, AnimeTrackingStatus.Following);
            var state = new PersonalFeatureStateStore(fixture.Factory);
            var page = new PastSeasonPage(source, new CacheService(fixture.Factory),
                new DragDropService(tracking, state, NullLogger<DragDropService>.Instance), tracking,
                fixture.Navigation, NullLogger<PastSeasonPage>.Instance) { Width = 960, Height = 720 };
            var rows = (ItemsRepeater)page.FindName("CatalogRows");
            try
            {
                await page.OnNavigatedToAsync(new PastSeasonTarget(2023, Season.Spring));
                root.Children.Add(page);
                await WaitAsync(root, () => !page.ViewModel.IsLoading && page.ViewModel.LoadedAnime.Count == 2);
                await page.ViewModel.ReloadStatusesAsync();
                await AssertCardsAsync(root, page, 2023, requests);
                await SelectThroughFlyoutAsync(page, 2024, Season.Spring);
                await WaitAsync(root, () => requests.Contains(new(2024, Season.Spring)));
                if (!lateOldReply)
                {
                    await WaitAsync(root, () => !page.ViewModel.IsLoading);
                    await AssertCardsAsync(root, page, 2024, requests);
                }
                await SelectThroughFlyoutAsync(page, 2025, Season.Spring);
                await WaitAsync(root, () => requests.Contains(new(2025, Season.Spring)) && !page.ViewModel.IsLoading);
                await AssertCardsAsync(root, page, 2025, requests, singleNewEntry ? 1 : 2);
                output.WriteLine($"detailed={detailed}; lateOld={lateOldReply}; requests={string.Join(",", requests)}; vm={string.Join(",", page.ViewModel.Entries.Select(entry => entry.Anime.ID))}; cards={string.Join(",", VisibleCards(rows).Select(card => card.Anime!.ID))}");
                var expected = singleNewEntry ? new[] { 20251 } : new[] { 20251, 20252 };
                Assert.Equal(expected, VisibleCards(rows).Select(card => card.Anime!.ID).Order().ToArray());
                Assert.All(VisibleCards(rows), card => Assert.Equal(card.Anime!.Title,
                    ((TextBlock)card.FindName(detailed ? "DetailedTitle" : "TitleText")).Text));
                Assert.Equal(AnimeTrackingStatus.Following, VisibleCards(rows).Single(card => card.Anime!.ID == 20251).TrackingStatus);
                oldReply.TrySetResult(SeasonItems(2024));
                for (var i = 0; i < 20; i++) { root.UpdateLayout(); await Task.Delay(10); }
                Assert.Equal(expected, VisibleCards(rows).Select(card => card.Anime!.ID).Order().ToArray());
                if (singleNewEntry)
                {
                    var right = Children<ContentPresenter>(rows).Single(item => Grid.GetColumn(item) == 1
                        && ReferenceEquals(item.ContentTemplate, page.Resources["DesktopDetailedTemplate"]));
                    Assert.Null(right.Content);
                    Assert.Equal(Visibility.Collapsed, right.Visibility);
                }
            }
            finally
            {
                oldReply.TrySetResult(SeasonItems(2024));
                root.Children.Remove(page);
                DesktopPresentationPreferences.Apply(previous, DesktopPresentationPreferences.ShowPlanReminders);
                await Task.Delay(30);
            }
        });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task EmptyOrFailedSeasonReplacesThePreviousCardsAndCanLoadAgain(bool fail)
        => UnoHeadlessTestHost.RunAsync(async root =>
        {
            await using var fixture = await Fixture.CreateAsync();
            var response = new TaskCompletionSource<List<Anime>>(TaskCreationOptions.RunContinuationsAsynchronously);
            var source = new SeasonSource((year, _, _) => year == 2024 ? response.Task : Task.FromResult(SeasonItems(year)));
            var tracking = new TrackingService(fixture.Factory);
            var page = new PastSeasonPage(source, new CacheService(fixture.Factory),
                new DragDropService(tracking, new PersonalFeatureStateStore(fixture.Factory), NullLogger<DragDropService>.Instance),
                tracking, fixture.Navigation, NullLogger<PastSeasonPage>.Instance);
            var rows = (ItemsRepeater)page.FindName("CatalogRows");
            var hint = (TextBlock)page.FindName("SeasonSwitchHint");
            await page.OnNavigatedToAsync(new PastSeasonTarget(2023, Season.Spring));
            try
            {
                await AttachAsync(root, page);
                await WaitAsync(root, () => VisibleCards(rows).Length == 2);
                await SelectThroughFlyoutAsync(page, 2024, Season.Spring);
                Assert.Equal(Visibility.Visible, hint.Visibility);
                if (fail) response.SetException(new HttpRequestException("合成断网"));
                else response.SetResult([]);
                await WaitAsync(root, () => !page.ViewModel.IsLoading && VisibleCards(rows).Length == 0);
                Assert.Equal(fail, page.ViewModel.IsError);
                Assert.Equal(!fail, page.ViewModel.HasNoResults);
                Assert.Equal(Visibility.Collapsed, hint.Visibility);
                Assert.Contains("2024", ((TextBlock)page.FindName("SeasonSummary")).Text);
                await SelectThroughFlyoutAsync(page, 2025, Season.Spring);
                await WaitAsync(root, () => VisibleCards(rows).Length == 2 && rows.IsHitTestVisible);
                Assert.All(VisibleCards(rows), card => Assert.Equal(2025, card.Anime!.SeasonYear));
            }
            finally { response.TrySetResult([]); await DetachAsync(root, page); }
        });

    private static async Task SelectThroughFlyoutAsync(PastSeasonPage page, int year, Season season)
    {
        var picker = (Button)page.FindName("SeasonPickerButton");
        var flyout = Assert.IsType<Flyout>(picker.Flyout);
        flyout.ShowAt(picker);
        Children<NumberBox>((DependencyObject)flyout.Content).Single().Value = year;
        await Task.Delay(30);
        var button = Children<ToggleButton>((DependencyObject)flyout.Content).Single(item => Equals(item.Content, PastSeasonBrowse.SeasonName(season)));
        typeof(ButtonBase).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(button, null);
    }

    private async Task AssertCardsAsync(Grid root, PastSeasonPage page, int year, List<PastSeasonTarget> requests, int count = 2)
    {
        var rows = (ItemsRepeater)page.FindName("CatalogRows");
        var expected = Enumerable.Range(1, count).Select(id => year * 10 + id).ToArray();
        try { await WaitAsync(root, () => VisibleCards(rows).Select(card => card.Anime?.ID ?? -1).Order().SequenceEqual(expected)); }
        finally
        {
            output.WriteLine($"expectedYear={year}; requests={string.Join(",", requests)}; vmSeason={page.ViewModel.Season}; vm={string.Join(",", page.ViewModel.Entries.Select(entry => entry.Anime.ID))}; cards={string.Join(",", VisibleCards(rows).Select(card => card.Anime?.ID.ToString() ?? "null"))}");
            output.WriteLine($"rowContext={Describe((rows.TryGetElement(0) as FrameworkElement)?.DataContext)}; presenters={string.Join(";", Children<ContentPresenter>(rows).Where(item => ReferenceEquals(item.ContentTemplate, page.Resources["DesktopDetailedTemplate"])).Select(item => $"content={Describe(item.Content)}/context={Describe(item.DataContext)}/visibility={item.Visibility}"))}");
        }
    }

    private static string Describe(object? value) => value switch
    {
        PastSeasonEntry entry => entry.Anime.ID.ToString(),
        DesktopCatalogRow row => $"row({Describe(row.First)},{Describe(row.Second)})",
        _ => value?.GetType().Name ?? "null",
    };

    private static AnimeCard[] VisibleCards(ItemsRepeater rows)
        // 回收池中的旧模板仍在视觉树里，虽有尺寸却排在约 -10000 的位置。
        => Enumerable.Range(0, VisualTreeHelper.GetChildrenCount(rows))
            .Select(i => VisualTreeHelper.GetChild(rows, i)).OfType<UIElement>()
            .Where(row => rows.GetElementIndex(row) >= 0)
            .SelectMany(Children<AnimeCard>)
            .Where(card => card.IsLoaded && card.ActualWidth > 0 && card.ActualHeight > 0 && IsVisible(card, rows)).ToArray();

    private static bool IsVisible(DependencyObject element, ItemsRepeater rows)
    {
        if (rows.Visibility == Visibility.Collapsed || rows.Opacity == 0) return false;
        for (DependencyObject? current = element; current is not null && !ReferenceEquals(current, rows); current = VisualTreeHelper.GetParent(current))
            if (current is UIElement { Visibility: Visibility.Collapsed }) return false;
        return true;
    }

    private static List<Anime> SeasonItems(int year)
        => Enumerable.Range(1, 2).Select(id => Item(year * 10 + id, $"{year}春季作品{id}") with
        { SeasonYear = year, SeasonMonth = 4, AirDate = new DateOnly(year, 4, 1) }).ToList();

    private static async Task WaitAsync(Grid root, Func<bool> ready)
    {
        var timeout = System.Diagnostics.Stopwatch.StartNew();
        while (!ready() && timeout.Elapsed < TimeSpan.FromSeconds(5)) { root.UpdateLayout(); await Task.Delay(10); }
        Assert.True(ready(), "真实季度请求和卡片应在 5 秒内完成更新。");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task NestedRowTemplatesPassEachAnimeToItsOwnCompleteCard(bool detailed)
        => UnoHeadlessTestHost.RunAsync(async root =>
        {
            var source = new SeasonSource((_, _, _) => Task.FromResult(new List<Anime>()));
            var page = new PastSeasonPage(source, null!, null!, new TrackingService(null!), null!, NullLogger<PastSeasonPage>.Instance);
            var first = new PastSeasonEntry(Item(1, "第一部"), AnimeTrackingStatus.Watching);
            var second = new PastSeasonEntry(Item(2, "第二部"), AnimeTrackingStatus.Following);
            object row = detailed ? new DesktopCatalogRow(first, second, true) : new DesktopCatalogPosterRow { Entries = { first, second } };
            var presenter = new ContentPresenter
            {
                Content = row,
                DataContext = row,
                ContentTemplate = (DataTemplate)page.Resources[detailed ? "DesktopDetailedRowTemplate" : "DesktopPosterRowTemplate"],
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
            };
            root.Children.Add(presenter);
            await Task.Delay(50);
            root.UpdateLayout();
            var cards = Children<AnimeCard>(presenter).ToArray();
            Assert.Equal(2, cards.Length);
            Assert.Equal(new[] { 1, 2 }, cards.Select(card => card.Anime!.ID));
            Assert.Equal(new[] { AnimeTrackingStatus.Watching, AnimeTrackingStatus.Following }, cards.Select(card => card.TrackingStatus));
            foreach (var card in cards)
            {
                var title = (TextBlock)card.FindName(detailed ? "DetailedTitle" : "TitleText");
                Assert.Equal(card.Anime!.Title, title.Text);
                Assert.True(title.ActualWidth > 0);
                Assert.True(title.ActualHeight > 0);
                Assert.False(card.ShowQuickActions);
                if (detailed)
                    Assert.Equal(card.Anime.Description, ((TextBlock)card.FindName("DescriptionText")).Text);
            }
        });

    [Fact]
    public async Task LateCanceledSeasonDoesNotReplaceTheNewSeasonOrEndItsLoadingState()
    {
        var oldReply = new TaskCompletionSource<List<Anime>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var newReply = new TaskCompletionSource<List<Anime>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var vm = new PastSeasonViewModel(new SeasonSource((year, _, _) => year == 2025 ? oldReply.Task : newReply.Task),
            new TrackingService(null!), NullLogger.Instance);
        using var cancellation = new CancellationTokenSource();
        var old = vm.LoadPastSeasonAnimeAsync(2025, Season.Spring, cancellation.Token);
        cancellation.Cancel();
        vm.Suspend();
        var current = vm.LoadPastSeasonAnimeAsync(2026, Season.Summer);
        oldReply.SetResult([Item(1, "旧季度")]);
        await old.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(vm.IsLoading);
        Assert.Empty(vm.Entries);
        newReply.SetResult([Item(2, "新季度")]);
        await current.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(vm.IsLoading);
        Assert.Equal(new PastSeasonTarget(2026, Season.Summer), vm.Season);
        Assert.Equal(2, Assert.Single(vm.Entries).Anime.ID);
    }

    [Fact]
    public async Task SuspendingASuccessfulSeasonKeepsItsBrowsingConditionsAndRows()
    {
        var vm = new PastSeasonViewModel(new SeasonSource((_, _, _) => Task.FromResult(new List<Anime> { Item(1, "作品一"), Item(2, "作品二") })),
            new TrackingService(null!), NullLogger.Instance);
        await vm.LoadPastSeasonAnimeAsync(2026, Season.Summer);
        vm.SetQuery("作品");
        vm.SelectFormat(AnimeMediaFormat.Television);
        vm.SetSortKey(PastSeasonSortKey.Title);
        vm.ToggleSortDirection();
        vm.SetHideCompleted(true);
        var entries = vm.Entries;
        vm.Suspend();
        Assert.Same(entries, vm.Entries);
        Assert.Equal("作品", vm.Query);
        Assert.Equal(AnimeMediaFormat.Television, vm.SelectedFormat);
        Assert.Equal(PastSeasonSortKey.Title, vm.SortKey);
        Assert.False(vm.SortAscending);
        Assert.True(vm.HideCompleted);
    }

    private static IEnumerable<T> Children<T>(DependencyObject parent) where T : DependencyObject
    {
        if (parent is T item) yield return item;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); ++index)
            foreach (var child in Children<T>(VisualTreeHelper.GetChild(parent, index))) yield return child;
    }

    private static Anime Item(int id, string title) => new(id, title, null, [], new DateOnly(2026, 7, 1), null,
        "实际模板使用的合成简介。", 2026, 7, Score: 8.7, MediaFormat: AnimeMediaFormat.Television);

    private sealed class SeasonSource(Func<int, Season, CancellationToken, Task<List<Anime>>> load) : IAnimeDataSource
    {
        public Task<List<Anime>> GetAnimeBySeasonAsync(int year, Season season, CancellationToken ct) => load(year, season, ct);
        public Task<List<Anime>> GetCurrentBroadcastScheduleAsync(CancellationToken ct) => throw new NotSupportedException();
        public Task<Anime?> GetAnimeDetailAsync(int animeID, CancellationToken ct) => throw new NotSupportedException();
        public Task<List<Studio>> GetStudioAsync(int animeID, CancellationToken ct) => throw new NotSupportedException();
        public Task<List<Tag>> GetTagsAsync(int animeID, CancellationToken ct) => throw new NotSupportedException();
        public Task<List<VoiceActor>> GetCVsAsync(int animeID, CancellationToken ct) => throw new NotSupportedException();
        public Task<List<CharacterRole>> GetCharacterRolesAsync(int animeID, CancellationToken ct) => throw new NotSupportedException();
        public Task<(List<Anime> Results, int Total)> SearchByTagAsync(string tag, int offset, string sort, CancellationToken ct, string? airDateFrom = null, string? airDateTo = null) => throw new NotSupportedException();
        public Task<List<PersonWork>> GetPersonWorksAsync(int personId, CancellationToken ct) => throw new NotSupportedException();
        public Task<(List<Anime> Results, int Total, int NextOffset)> SearchByKeywordAsync(string keyword, int offset, CancellationToken ct) => throw new NotSupportedException();
    }
}
