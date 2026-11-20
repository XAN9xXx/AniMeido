using System.Numerics;

namespace AniMeido.App.Services;

internal readonly record struct DesktopLoadingDotFrame(Vector2 Center, Vector2 Radius, float Opacity);
internal readonly record struct DesktopLoadingLineFrame(Vector2 Start, Vector2 End);
internal readonly record struct DesktopLoadingArcFrame(float TrimStart, float TrimEnd, float Rotation);

internal sealed partial class DesktopLoadingMotion(float canvasSize, double period, DesktopLoadingDot[] dots, bool connected = false)
{
    internal float CanvasSize { get; } = canvasSize;
    internal double Period { get; } = period;
    internal int DotCount => dots.Length;
    internal int LineCount => connected ? 4 : 0;
    internal bool IsArc => dots.Length == 0;

    internal static DesktopLoadingMotion For(DesktopLoadingAnimation animation) => animation switch
    {
        DesktopLoadingAnimation.FiveDots => FiveDotsMotion,
        DesktopLoadingAnimation.Arc => ArcMotion,
        DesktopLoadingAnimation.ClaudeNodes => NodesMotion,
        DesktopLoadingAnimation.ClaudeOrbit => OrbitMotion,
        DesktopLoadingAnimation.ClaudeBreath => BreathMotion,
        DesktopLoadingAnimation.ClaudeQuiet => QuietMotion,
        DesktopLoadingAnimation.Gemini => GeminiMotion,
        _ => throw new ArgumentOutOfRangeException(nameof(animation)),
    };

    private static readonly DesktopLoadingMotion ArcMotion = new(80, 2, []);

    internal void SampleStill(Span<DesktopLoadingDotFrame> output, out DesktopLoadingArcFrame arc)
    {
        output.Clear();
        arc = new(0, .25f, -90);
        if (IsArc) return;
        if (DotCount == 5)
        {
            for (var index = 0; index < 5; index++)
            {
                var angle = index * MathF.Tau / 5;
                output[index] = new(new Vector2(16 + MathF.Sin(angle) * 14, 16 - MathF.Cos(angle) * 14), new Vector2(2), 1);
            }
            return;
        }
        if (CanvasSize == 28)
        {
            for (var index = 0; index < dots.Length; index++)
                output[index] = dots[index].Sample(0, 0, CanvasSize);
            return;
        }
        output[0] = new(new Vector2(.5f, 17f / 120) * CanvasSize, new Vector2(.075f * CanvasSize), 1);
        output[1] = new(new Vector2(97.239f / 120, 81.5f / 120) * CanvasSize, new Vector2(.075f * CanvasSize), 1);
        output[2] = new(new Vector2(22.761f / 120, 81.5f / 120) * CanvasSize, new Vector2(.075f * CanvasSize), 1);
    }

    internal void Sample(double elapsed, Span<DesktopLoadingDotFrame> output,
        Span<DesktopLoadingLineFrame> lines, out DesktopLoadingArcFrame arc)
    {
        var time = Math.Max(0, elapsed);
        var cycle = time % Period;
        for (var index = 0; index < dots.Length; index++)
            output[index] = dots[index].Sample(time, cycle, CanvasSize);
        for (var index = 0; index < LineCount; index++)
            lines[index] = new(output[index].Center, output[(index + 1) % 4].Center);

        // Uno 7 的内置 ProgressRing：两秒旋转 900 度，前半轮伸展、后半轮收拢。
        // 轨迹来自固定版本的 AnimatedVisuals/ProgressRingIndeterminate.cs。
        var progress = (float)(cycle / Period);
        arc = new(Math.Max(0, progress - .5f), Math.Min(.5f, Math.Max(.0001f, progress)), progress * 900);
    }
}

internal sealed class DesktopLoadingDot(
    Vector2 center, Vector2 radius,
    DesktopLoadingTrack? x = null, DesktopLoadingTrack? y = null,
    DesktopLoadingTrack? rx = null, DesktopLoadingTrack? ry = null,
    DesktopLoadingTrack? opacity = null, DesktopLoadingTrack? rotation = null)
{
    internal DesktopLoadingDotFrame Sample(double elapsed, double cycle, float canvasSize)
    {
        var position = center + new Vector2(x?.Sample(elapsed, cycle) ?? 0, y?.Sample(elapsed, cycle) ?? 0);
        if (rotation is not null)
        {
            var angle = rotation.Sample(elapsed, cycle) * (MathF.PI / 180);
            var origin = new Vector2(canvasSize / 2);
            var delta = position - origin;
            var (sin, cos) = MathF.SinCos(angle);
            position = origin + new Vector2(delta.X * cos - delta.Y * sin, delta.X * sin + delta.Y * cos);
        }
        return new(position, new Vector2(rx?.Sample(elapsed, cycle) ?? radius.X,
            ry?.Sample(elapsed, cycle) ?? radius.Y), opacity?.Sample(elapsed, cycle) ?? 1);
    }
}

// 数据每三个数为：时间、数值、缓动编号。整个轨迹共用绝对时钟，短帧不会累积调度延迟。
internal sealed class DesktopLoadingTrack(double[] samples, double delay = 0, float initial = 0, double repeat = 0)
{
    internal float Sample(double elapsed, double cycle)
    {
        var time = (repeat > 0 ? elapsed : cycle) - delay;
        if (time < 0)
            return initial;
        if (repeat > 0)
            time %= repeat;
        var count = samples.Length / 3;
        if (time >= samples[^3])
            return (float)samples[^2];
        var low = 0;
        var high = count - 1;
        while (low < high)
        {
            var mid = (low + high) / 2;
            if (samples[mid * 3] < time) low = mid + 1;
            else high = mid;
        }
        var offset = low * 3;
        var end = samples[offset];
        var start = low == 0 ? 0 : samples[offset - 3];
        var from = low == 0 ? initial : samples[offset - 2];
        var to = samples[offset + 1];
        if (time >= end || end <= start)
            return (float)to;
        var eased = Ease((time - start) / (end - start), (int)samples[offset + 2]);
        return (float)(from + (to - from) * eased);
    }

    private static double Ease(double fraction, int curve)
    {
        if (curve == 0) return fraction;
        if (curve == 1) return 0;
        var (x1, y1, x2, y2) = curve switch
        {
            2 => (.13, .21, .1, .7),
            3 => (.02, .33, .38, .77),
            4 => (.57, .17, .95, .75),
            5 => (0d, .19, .07, .72),
            6 => (0d, 0d, .95, .37),
            7 => (.4, 0d, .2, 1d),
            _ => throw new ArgumentOutOfRangeException(nameof(curve)),
        };
        var low = 0d;
        var high = 1d;
        for (var index = 0; index < 18; index++)
        {
            var t = (low + high) / 2;
            if (Bezier(t, x1, x2) < fraction) low = t;
            else high = t;
        }
        return Bezier((low + high) / 2, y1, y2);
    }

    private static double Bezier(double t, double a, double b)
        => 3 * (1 - t) * (1 - t) * t * a + 3 * (1 - t) * t * t * b + t * t * t;
}
