using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using AniMeido.Plugin.Base.Views.Controls;
using Windows.Foundation;
using Windows.System;
using Windows.UI.Input.Preview.Injection;
using Xunit;

namespace AniMeido.Desktop.Tests;

[Collection(UnoHeadlessTestHost.CollectionName)]
public sealed class DesktopWheelShelfTests
{
    [Fact]
    public Task MovingOntoAShelfAndReversingDoesNotRestartAVerticalSequence()
        => UnoHeadlessTestHost.RunAsync(async root =>
        {
            var shelf = Shelf(900);
            var scene = new Scene(shelf);
            await scene.AttachAsync(root);
            scene.Body.ChangeView(null, 120, null, true);
            var mouse = new Mouse(root);
            mouse.MoveTo(scene.Header, 80, 170);
            mouse.Tick(-120);
            await UntilAsync(() => scene.Body.VerticalOffset > 135);
            var down = scene.Body.VerticalOffset;

            mouse.MoveTo(shelf);
            mouse.Tick(120);
            await UntilAsync(() => scene.Body.VerticalOffset < down - 10);
            Assert.Equal(0, shelf.HorizontalOffset);

            mouse.MoveTo(shelf);
            mouse.Tick(-120, elapsed: 250);
            mouse.Tick(-120);
            await UntilAsync(() => shelf.HorizontalOffset == 240);
        });

    [Fact]
    public Task PendingTargetHandsOffAtTheEdgeAndReverseCanUseTheSameShelf()
        => UnoHeadlessTestHost.RunAsync(async root =>
        {
            var shelf = Shelf(540);
            var scene = new Scene(shelf);
            await scene.AttachAsync(root);
            var limit = shelf.ScrollableWidth;
            Assert.InRange(limit, 120, 239);
            var mouse = new Mouse(root);
            mouse.MoveTo(shelf);
            var handled = new List<bool>();
            shelf.AddHandler(UIElement.PointerWheelChangedEvent, new PointerEventHandler((_, args) => handled.Add(args.Handled)), true);

            mouse.Tick(-120);
            mouse.Tick(-120);
            mouse.Tick(-120);
            Assert.Equal([true, true, false], handled);
            await UntilAsync(() => shelf.HorizontalOffset == limit && scene.Body.VerticalOffset > 15);

            mouse.MoveTo(shelf, y: 70);
            mouse.Tick(120);
            Assert.True(handled[^1]);
            await UntilAsync(() => shelf.HorizontalOffset == limit - 120);

            mouse.Tick(120);
            mouse.Tick(120);
            Assert.Equal([true, false], handled.TakeLast(2));
            await UntilAsync(() => shelf.HorizontalOffset == 0);
        });

    [Fact]
    public Task NestedShelvesDoNotStealAnActiveSequenceFromTheInnerShelf()
        => UnoHeadlessTestHost.RunAsync(async root =>
        {
            var inner = Shelf(360, width: 240);
            var outer = Shelf(950, child: inner);
            var scene = new Scene(outer);
            await scene.AttachAsync(root);
            Assert.Equal(120, inner.ScrollableWidth);
            Assert.True(outer.ScrollableWidth > 120);
            var mouse = new Mouse(root);
            mouse.MoveTo(inner);

            mouse.Tick(-120);
            mouse.Tick(-120);
            await UntilAsync(() => inner.HorizontalOffset == 120 && scene.Body.VerticalOffset > 20);
            Assert.Equal(0, outer.HorizontalOffset);

            inner.Content = Blocks(160);
            root.UpdateLayout();
            Assert.Equal(0, inner.ScrollableWidth);
            mouse.MoveTo(inner, y: 60);
            mouse.Tick(-120, elapsed: 300);
            await UntilAsync(() => outer.HorizontalOffset == 120);
        });

    [Fact]
    public Task ANoOverflowShelfAndAnEditableControlLeaveTheVerticalRouteAlone()
        => UnoHeadlessTestHost.RunAsync(async root =>
        {
            var fit = Shelf(200);
            var editor = new TextBox { Width = 230, Height = 82, Text = "我的搜索词" };
            var wide = Shelf(900, child: editor);
            var scene = new Scene(fit, wide);
            await scene.AttachAsync(root);
            var mouse = new Mouse(root);
            Assert.Equal(0, fit.ScrollableWidth);
            mouse.MoveTo(fit);
            mouse.Tick(-120);
            await UntilAsync(() => scene.Body.VerticalOffset > 20);

            mouse.MoveTo(editor);
            var before = scene.Body.VerticalOffset;
            mouse.Tick(-120, elapsed: 300);
            await UntilAsync(() => scene.Body.VerticalOffset > before + 20);
            Assert.Equal(0, wide.HorizontalOffset);
            Assert.Equal("我的搜索词", editor.Text);
        });

    [Fact]
    public Task NativeHorizontalAndShiftWheelReachTheShelfEvenDuringAVerticalSequence()
        => UnoHeadlessTestHost.RunAsync(async root =>
        {
            var shelf = Shelf(950);
            var scene = new Scene(shelf);
            await scene.AttachAsync(root);
            var mouse = new Mouse(root);
            mouse.MoveTo(scene.Header);
            mouse.Tick(-120);
            await UntilAsync(() => scene.Body.VerticalOffset > 15);

            mouse.MoveTo(shelf);
            mouse.Tick(120, horizontal: true);
            await UntilAsync(() => shelf.HorizontalOffset > 25);
            mouse.Key(VirtualKey.Shift);
            try
            {
                mouse.MoveTo(shelf);
                var before = shelf.HorizontalOffset;
                mouse.Tick(-120);
                await UntilAsync(() => shelf.HorizontalOffset > before + 25);
            }
            finally { mouse.Key(VirtualKey.Shift, up: true); }

            var plainWheelHandled = new List<bool>();
            shelf.AddHandler(UIElement.PointerWheelChangedEvent, new PointerEventHandler((_, args) => plainWheelHandled.Add(args.Handled)), true);
            mouse.MoveTo(shelf, y: 70);
            mouse.Tick(-120);
            Assert.Equal([false], plainWheelHandled);
        });

    [Fact]
    public Task PopupWheelScrollsOnlyItsOwnContentAndDoesNotStartAPageGesture()
        => UnoHeadlessTestHost.RunAsync(async root =>
        {
            var popupBody = new ScrollViewer
            {
                Width = 260, Height = 105, Content = new Border { Height = 480, Background = new SolidColorBrush(Colors.Gray) },
                VerticalScrollMode = ScrollMode.Enabled, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
            };
            var popup = new Popup { Child = popupBody, HorizontalOffset = 25, VerticalOffset = 20 };
            var shelf = Shelf(900, child: popup);
            var scene = new Scene(shelf);
            await scene.AttachAsync(root);
            var mouse = new Mouse(root, includePopups: true);
            popup.XamlRoot = root.XamlRoot;
            popup.IsOpen = true;
            try
            {
                await UntilAsync(() => popupBody.IsLoaded && popupBody.ScrollableHeight > 0);
                root.UpdateLayout();
                var popupHits = 0;
                popupBody.AddHandler(UIElement.PointerWheelChangedEvent, new PointerEventHandler((_, _) => popupHits++), true);
                mouse.MoveTo(popupBody);
                mouse.Tick(-120);
                Assert.Equal(1, popupHits);
                await UntilAsync(() => popupBody.VerticalOffset > 0);
                Assert.Equal(0, scene.Body.VerticalOffset);
                Assert.Equal(0, shelf.HorizontalOffset);
            }
            finally { popup.IsOpen = false; }
            await UntilAsync(() => !popupBody.IsLoaded);
            mouse.MoveTo(shelf);
            mouse.Tick(-120);
            await UntilAsync(() => shelf.HorizontalOffset == 120);
        });

    [Fact]
    public Task TouchDragStillUsesTheNativeHorizontalScrollRoute()
        => UnoHeadlessTestHost.RunAsync(async root =>
        {
            var shelf = Shelf(900);
            var scene = new Scene(shelf);
            await scene.AttachAsync(root);
            var mouse = new Mouse(root);
            mouse.MoveTo(scene.Header);
            mouse.Tick(-120);
            await UntilAsync(() => scene.Body.VerticalOffset > 20);

            var anchor = shelf.TransformToVisual(root).TransformPoint(new Point(270, 65));
            var touch = Assert.IsType<InputInjector>(InputInjector.TryCreate(root));
            const InjectedInputPointerOptions contact = InjectedInputPointerOptions.FirstButton | InjectedInputPointerOptions.InContact;
            touch.InitializeTouchInjection(InjectedInputVisualizationMode.Default);
            try
            {
                touch.InjectTouchInput([
                    TouchAt(0, contact | InjectedInputPointerOptions.New | InjectedInputPointerOptions.PointerDown),
                    TouchAt(45, contact | InjectedInputPointerOptions.Update),
                    TouchAt(95, contact | InjectedInputPointerOptions.Update),
                    TouchAt(140, contact | InjectedInputPointerOptions.Update),
                    TouchAt(140, InjectedInputPointerOptions.FirstButton | InjectedInputPointerOptions.PointerUp),
                ]);
                await UntilAsync(() => shelf.HorizontalOffset > 65);
            }
            finally { touch.UninitializeTouchInjection(); }

            InjectedInputTouchInfo TouchAt(int distance, InjectedInputPointerOptions flags)
                => new()
                {
                    PointerInfo = new()
                    {
                        PointerId = 42, PointerOptions = flags, TimeOffsetInMilliseconds = 30,
                        PixelLocation = new() { PositionX = (int)anchor.X - distance, PositionY = (int)anchor.Y },
                    },
                };
        });

    [Fact]
    public Task ReplacingAndReattachingAPageDoesNotCarryItsWheelSequence()
        => UnoHeadlessTestHost.RunAsync(async root =>
        {
            var firstShelf = Shelf(900);
            var first = new Scene(firstShelf);
            await first.AttachAsync(root);
            var mouse = new Mouse(root);
            mouse.MoveTo(first.Header);
            mouse.Tick(-120);
            Assert.True(root.Children.Remove(first.Page));
            await UntilAsync(() => !firstShelf.IsLoaded);

            var secondShelf = Shelf(900);
            var second = new Scene(secondShelf);
            await second.AttachAsync(root);
            mouse.MoveTo(secondShelf);
            mouse.Tick(-120);
            await UntilAsync(() => secondShelf.HorizontalOffset == 120);

            Assert.True(root.Children.Remove(second.Page));
            await UntilAsync(() => !secondShelf.IsLoaded);
            await first.AttachAsync(root);
            mouse.MoveTo(firstShelf);
            mouse.Tick(-120);
            await UntilAsync(() => firstShelf.HorizontalOffset == 120);
        });

    private static DesktopWheelShelf Shelf(double extent, double width = double.NaN, UIElement? child = null)
        => new()
        {
            Width = width, Height = 100,
            Content = Blocks(extent, child),
            HorizontalScrollMode = ScrollMode.Enabled, HorizontalScrollBarVisibility = ScrollBarVisibility.Hidden,
            VerticalScrollMode = ScrollMode.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalAlignment = HorizontalAlignment.Stretch,
        };

    private static FrameworkElement Blocks(double width, UIElement? child = null)
    {
        var panel = new Grid { Width = width, Height = 100, Background = new SolidColorBrush(Colors.Transparent) };
        if (child is not null)
        {
            if (child is FrameworkElement element) element.HorizontalAlignment = HorizontalAlignment.Left;
            panel.Children.Add(child);
        }
        return panel;
    }

    private static async Task UntilAsync(Func<bool> done)
    {
        for (var frame = 0; frame < 125 && !done(); frame++) await Task.Delay(16);
        Assert.True(done(), "滚动容器未到达预期偏移。");
    }

    private sealed class Scene
    {
        internal Border Header { get; } = new() { Height = 250, Background = new SolidColorBrush(Colors.Transparent) };
        internal ScrollViewer Body { get; }
        internal Page Page { get; }

        internal Scene(params DesktopWheelShelf[] shelves)
        {
            var stack = new StackPanel();
            stack.Children.Add(Header);
            foreach (var shelf in shelves) stack.Children.Add(shelf);
            stack.Children.Add(new Border { Height = 800 });
            Body = new ScrollViewer
            {
                Content = stack, VerticalScrollMode = ScrollMode.Enabled, VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
                HorizontalScrollMode = ScrollMode.Disabled, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            };
            Page = new Page { Content = Body, Width = 380, Height = 570 };
        }

        internal async Task AttachAsync(Grid root)
        {
            root.Children.Add(Page);
            root.UpdateLayout();
            await UntilAsync(() => Page.IsLoaded && Body.ScrollableHeight > 0);
        }
    }

    private sealed class Mouse(Grid root, bool includePopups = false)
    {
        private readonly InputInjector _injector = Assert.IsType<InputInjector>(
            includePopups ? InputInjector.TryCreate() : InputInjector.TryCreate(root));
        private int _x, _y;

        internal void MoveTo(FrameworkElement target, int x = 35, int y = 24)
        {
            var point = target.TransformToVisual(root).TransformPoint(new Point(x, y));
            _injector.InjectMouseInput([new()
            {
                MouseOptions = InjectedInputMouseOptions.Move,
                DeltaX = (int)point.X - _x, DeltaY = (int)point.Y - _y,
            }]);
            (_x, _y) = ((int)point.X, (int)point.Y);
        }

        internal void Tick(int delta, uint elapsed = 25, bool horizontal = false)
            => _injector.InjectMouseInput([new()
            {
                MouseOptions = horizontal ? InjectedInputMouseOptions.HWheel : InjectedInputMouseOptions.Wheel,
                DeltaY = delta, DeltaX = delta, TimeOffsetInMilliseconds = elapsed,
            }]);

        internal void Key(VirtualKey key, bool up = false)
            => _injector.InjectKeyboardInput([new()
            {
                VirtualKey = (ushort)key, KeyOptions = up ? InjectedInputKeyOptions.KeyUp : 0,
            }]);
    }
}
