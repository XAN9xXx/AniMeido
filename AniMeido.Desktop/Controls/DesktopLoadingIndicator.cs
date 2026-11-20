using System.Diagnostics;
using System.Numerics;
using AniMeido.App.Services;
using AniMeido.Plugin.Base.Services;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.UI;

namespace AniMeido.App.Controls;

public sealed class DesktopLoadingIndicator : FrameworkElement
{
    public static readonly DependencyProperty IsActiveProperty = DependencyProperty.Register(
        nameof(IsActive), typeof(bool), typeof(DesktopLoadingIndicator), new PropertyMetadata(false, OnPlaybackChanged));
    public static readonly DependencyProperty MotionEnabledProperty = DependencyProperty.Register(
        nameof(MotionEnabled), typeof(bool), typeof(DesktopLoadingIndicator), new PropertyMetadata(true, OnPlaybackChanged));
    public static readonly DependencyProperty ForegroundProperty = DependencyProperty.Register(
        nameof(Foreground), typeof(SolidColorBrush), typeof(DesktopLoadingIndicator), new PropertyMetadata(null, OnForegroundChanged));

    private readonly DesktopLoadingSession _selection;
    private readonly Stopwatch _clock = new();
    private readonly DesktopLoadingDotFrame[] _dots = new DesktopLoadingDotFrame[5];
    private readonly DesktopLoadingLineFrame[] _lines = new DesktopLoadingLineFrame[4];
    private readonly CompositionEllipseGeometry?[] _dotGeometry = new CompositionEllipseGeometry?[5];
    private readonly ShapeVisual?[] _dotVisuals = new ShapeVisual?[5];
    private readonly CompositionLineGeometry?[] _lineGeometry = new CompositionLineGeometry?[4];
    private readonly ShapeVisual?[] _lineVisuals = new ShapeVisual?[4];
    private readonly CompositionSpriteShape?[] _shapes = new CompositionSpriteShape?[10];
    private ContainerVisual? _container;
    private CompositionColorBrush? _brush;
    private ShapeVisual? _arcVisual;
    private CompositionEllipseGeometry? _arcGeometry;
    private DesktopLoadingMotion? _motion;
    private SolidColorBrush? _observedForeground;
    private long _colorCallback;
    private long _opacityCallback;
    private bool _rendering;

    public DesktopLoadingIndicator() : this(DesktopLoadingSelection.RandomAnimation) { }

    internal DesktopLoadingIndicator(Func<DesktopLoadingAnimation> choose)
    {
        _selection = new DesktopLoadingSession(choose);
        IsHitTestVisible = false;
        AutomationProperties.SetName(this, "正在读取");
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        SizeChanged += (_, _) => UpdateBounds();
    }

    public bool IsActive { get => (bool)GetValue(IsActiveProperty); set => SetValue(IsActiveProperty, value); }
    public bool MotionEnabled { get => (bool)GetValue(MotionEnabledProperty); set => SetValue(MotionEnabledProperty, value); }
    public SolidColorBrush? Foreground { get => (SolidColorBrush?)GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }
    internal bool HasFrameSubscription => _rendering;
    internal DesktopLoadingAnimation CurrentAnimation => _selection.Animation;
    internal Color RenderedColor => _brush?.Color ?? Colors.Transparent;
    internal int MeasureCount { get; private set; }
    internal long RenderedFrameCount { get; private set; }

    protected override Size MeasureOverride(Size availableSize)
    {
        MeasureCount++;
        return new Size(Math.Min(32, availableSize.Width), Math.Min(32, availableSize.Height));
    }

    protected override AutomationPeer OnCreateAutomationPeer() => new LoadingPeer(this);

    private static void OnPlaybackChanged(DependencyObject element, DependencyPropertyChangedEventArgs args)
    {
        var indicator = (DesktopLoadingIndicator)element;
        indicator._selection.Update(indicator.IsActive);
        indicator.Synchronize();
    }

    private static void OnForegroundChanged(DependencyObject element, DependencyPropertyChangedEventArgs args)
    {
        var indicator = (DesktopLoadingIndicator)element;
        indicator.ObserveForeground();
        indicator.UpdateColor();
    }

    private void OnLoaded(object sender, RoutedEventArgs args)
    {
        DesktopPresentationPreferences.AppearanceChanged += OnAppearanceChanged;
        ReadMotionPreference();
        _selection.Update(IsActive);
        ObserveForeground();
        Synchronize();
    }

    private void OnUnloaded(object sender, RoutedEventArgs args)
    {
        DesktopPresentationPreferences.AppearanceChanged -= OnAppearanceChanged;
        StopFrames();
        StopObservingForeground();
        ElementCompositionPreview.SetElementChildVisual(this, null);
        foreach (var shape in _shapes) shape?.Dispose();
        foreach (var geometry in _dotGeometry) geometry?.Dispose();
        foreach (var geometry in _lineGeometry) geometry?.Dispose();
        foreach (var visual in _dotVisuals) visual?.Dispose();
        foreach (var visual in _lineVisuals) visual?.Dispose();
        _arcGeometry?.Dispose();
        _arcVisual?.Dispose();
        _brush?.Dispose();
        _container?.Dispose();
        Array.Clear(_shapes);
        Array.Clear(_dotGeometry);
        Array.Clear(_lineGeometry);
        Array.Clear(_dotVisuals);
        Array.Clear(_lineVisuals);
        _arcGeometry = null;
        _arcVisual = null;
        _brush = null;
        _container = null;
    }

    private void OnAppearanceChanged(object? sender, EventArgs args) => ReadMotionPreference();

    private void ReadMotionPreference()
    {
        if (Application.Current.Resources.TryGetValue("DesktopMotionEnabled", out var enabled)
            && enabled is bool value)
            MotionEnabled = value;
    }

    private void Synchronize()
    {
        if (!IsLoaded || !IsActive)
        {
            StopFrames();
            if (_container is not null) _container.IsVisible = false;
            return;
        }
        _motion = DesktopLoadingMotion.For(_selection.Animation);
        CreateVisuals();
        _container!.IsVisible = true;
        UpdateBounds();
        UpdateColor();
        if (!MotionEnabled)
        {
            StopFrames();
            DrawFrame(0, still: true);
            return;
        }
        if (_rendering) return;
        _clock.Restart();
        DrawFrame(0, still: false);
        CompositionTarget.Rendering += OnRendering;
        _rendering = true;
    }

    private void StopFrames()
    {
        if (_rendering)
            CompositionTarget.Rendering -= OnRendering;
        _rendering = false;
        _clock.Stop();
    }

    private void OnRendering(object? sender, object args)
    {
        RenderedFrameCount++;
        DrawFrame(_clock.Elapsed.TotalSeconds, still: false);
    }

    internal void DrawFrame(double elapsed, bool still)
    {
        if (_motion is null || _container is null) return;
        DesktopLoadingArcFrame arc;
        if (still) _motion.SampleStill(_dots, out arc);
        else _motion.Sample(elapsed, _dots, _lines, out arc);
        for (var index = 0; index < _dotVisuals.Length; index++)
        {
            var visual = _dotVisuals[index]!;
            visual.IsVisible = index < _motion.DotCount;
            if (!visual.IsVisible) continue;
            _dotGeometry[index]!.Center = _dots[index].Center;
            _dotGeometry[index]!.Radius = _dots[index].Radius;
            visual.Opacity = _dots[index].Opacity;
        }
        for (var index = 0; index < _lineVisuals.Length; index++)
        {
            var visual = _lineVisuals[index]!;
            visual.IsVisible = !still && index < _motion.LineCount;
            if (!visual.IsVisible) continue;
            _lineGeometry[index]!.Start = _lines[index].Start;
            _lineGeometry[index]!.End = _lines[index].End;
        }
        _arcVisual!.IsVisible = _motion.IsArc;
        if (_motion.IsArc)
        {
            _arcGeometry!.TrimStart = arc.TrimStart;
            _arcGeometry.TrimEnd = arc.TrimEnd;
            _arcVisual.RotationAngleInDegrees = arc.Rotation;
        }
    }

    private void CreateVisuals()
    {
        if (_container is not null) return;
        var compositor = ElementCompositionPreview.GetElementVisual(this).Compositor;
        _container = compositor.CreateContainerVisual();
        _brush = compositor.CreateColorBrush();
        for (var index = 0; index < _lineVisuals.Length; index++)
        {
            var geometry = _lineGeometry[index] = compositor.CreateLineGeometry();
            var shape = _shapes[index] = compositor.CreateSpriteShape(geometry);
            shape.StrokeBrush = _brush;
            shape.StrokeThickness = 3;
            shape.StrokeStartCap = shape.StrokeEndCap = CompositionStrokeCap.Round;
            var visual = _lineVisuals[index] = compositor.CreateShapeVisual();
            visual.Shapes.Add(shape);
            _container.Children.InsertAtTop(visual);
        }
        for (var index = 0; index < _dotVisuals.Length; index++)
        {
            var geometry = _dotGeometry[index] = compositor.CreateEllipseGeometry();
            var shape = _shapes[index + 4] = compositor.CreateSpriteShape(geometry);
            shape.FillBrush = _brush;
            var visual = _dotVisuals[index] = compositor.CreateShapeVisual();
            visual.Shapes.Add(shape);
            _container.Children.InsertAtTop(visual);
        }
        _arcGeometry = compositor.CreateEllipseGeometry();
        _arcGeometry.Center = new Vector2(40);
        _arcGeometry.Radius = new Vector2(35);
        var arcShape = _shapes[9] = compositor.CreateSpriteShape(_arcGeometry);
        arcShape.StrokeBrush = _brush;
        arcShape.StrokeThickness = 7.5f;
        arcShape.StrokeStartCap = arcShape.StrokeEndCap = CompositionStrokeCap.Round;
        _arcVisual = compositor.CreateShapeVisual();
        _arcVisual.CenterPoint = new Vector3(40, 40, 0);
        _arcVisual.Shapes.Add(arcShape);
        _container.Children.InsertAtTop(_arcVisual);
        ElementCompositionPreview.SetElementChildVisual(this, _container);
    }

    private void UpdateBounds()
    {
        if (_container is null || _motion is null) return;
        var size = _motion.CanvasSize;
        var width = ActualWidth > 0 ? (float)ActualWidth : 32;
        var height = ActualHeight > 0 ? (float)ActualHeight : 32;
        var scale = Math.Min(width, height) / size;
        _container.Size = new Vector2(size);
        _container.Scale = new Vector3(scale, scale, 1);
        _container.Offset = new Vector3((width - size * scale) / 2, (height - size * scale) / 2, 0);
        foreach (var visual in _dotVisuals) visual!.Size = new Vector2(size);
        foreach (var visual in _lineVisuals) visual!.Size = new Vector2(size);
        _arcVisual!.Size = new Vector2(size);
    }

    private void ObserveForeground()
    {
        StopObservingForeground();
        if (!IsLoaded || Foreground is not { } brush) return;
        _observedForeground = brush;
        _colorCallback = brush.RegisterPropertyChangedCallback(SolidColorBrush.ColorProperty, (_, _) => UpdateColor());
        _opacityCallback = brush.RegisterPropertyChangedCallback(Brush.OpacityProperty, (_, _) => UpdateColor());
    }

    private void StopObservingForeground()
    {
        if (_observedForeground is null) return;
        _observedForeground.UnregisterPropertyChangedCallback(SolidColorBrush.ColorProperty, _colorCallback);
        _observedForeground.UnregisterPropertyChangedCallback(Brush.OpacityProperty, _opacityCallback);
        _observedForeground = null;
    }

    private void UpdateColor()
    {
        if (_brush is null) return;
        var color = Foreground?.Color ?? Colors.Gray;
        color.A = (byte)Math.Round(color.A * (Foreground?.Opacity ?? 1));
        _brush.Color = color;
    }

    private sealed class LoadingPeer(DesktopLoadingIndicator owner) : FrameworkElementAutomationPeer(owner)
    {
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.ProgressBar;
        protected override string GetClassNameCore() => nameof(DesktopLoadingIndicator);
    }
}
