using System.Numerics;
using AniMeido.App.Services;
using Xunit;

namespace AniMeido.Desktop.Tests;

public sealed class DesktopLoadingMotionTests
{
    [Theory]
    [InlineData(DesktopLoadingAnimation.ClaudeOrbit, 0, 0, 8.339, 24.123)]
    [InlineData(DesktopLoadingAnimation.ClaudeOrbit, 1.833333, 0, 22.781, 24.269)]
    [InlineData(DesktopLoadingAnimation.ClaudeBreath, .666667, 0, 12.576, 24.13)]
    [InlineData(DesktopLoadingAnimation.ClaudeNodes, 4.3, 0, 78.128, 22.263)]
    [InlineData(DesktopLoadingAnimation.ClaudeNodes, 4.3, 3, 25.24, 50.8)]
    [InlineData(DesktopLoadingAnimation.Gemini, 0, 0, 20.6, 9)]
    [InlineData(DesktopLoadingAnimation.Gemini, 0, 1, 14, 11.037)]
    [InlineData(DesktopLoadingAnimation.Gemini, 0, 2, 7.4, 17.744)]
    [InlineData(DesktopLoadingAnimation.Gemini, 24d / 60.0914611816406, 1, 14, 19)]
    [InlineData(DesktopLoadingAnimation.Gemini, 235.002d / 60.0914611816406, 0, 4.724425, 11.734232)]
    [InlineData(DesktopLoadingAnimation.Gemini, 235.002d / 60.0914611816406, 1, 16.707443, 23.189429)]
    [InlineData(DesktopLoadingAnimation.Gemini, 235.002d / 60.0914611816406, 2, 20.729495, 6.405826)]
    [InlineData(DesktopLoadingAnimation.Gemini, 481.004d / 60.0914611816406, 0, 23.561048, 11.68422)]
    [InlineData(DesktopLoadingAnimation.Gemini, 536.004d / 60.0914611816406, 2, 7.4, 9)]
    public void ApprovedSamplesKeepTheirPositions(DesktopLoadingAnimation animation, double time, int index, double x, double y)
    {
        Span<DesktopLoadingDotFrame> dots = stackalloc DesktopLoadingDotFrame[5];
        Span<DesktopLoadingLineFrame> lines = stackalloc DesktopLoadingLineFrame[4];
        DesktopLoadingMotion.For(animation).Sample(time, dots, lines, out _);
        Assert.InRange(Math.Abs(dots[index].Center.X - x), 0, .00002);
        Assert.InRange(Math.Abs(dots[index].Center.Y - y), 0, .00002);
    }

    [Fact]
    public void ThreeToFourNodeLinesKeepTheirExactDotEndpoints()
    {
        var motion = DesktopLoadingMotion.For(DesktopLoadingAnimation.ClaudeNodes);
        Span<DesktopLoadingDotFrame> dots = stackalloc DesktopLoadingDotFrame[5];
        Span<DesktopLoadingLineFrame> lines = stackalloc DesktopLoadingLineFrame[4];
        for (var frame = 0; frame < 508; frame++)
        {
            motion.Sample(frame / 60d, dots, lines, out _);
            for (var index = 0; index < 4; index++)
            {
                Assert.Equal(dots[index].Center, lines[index].Start);
                Assert.Equal(dots[(index + 1) % 4].Center, lines[index].End);
            }
        }
        motion.Sample(4.275, dots, lines, out _);
        Assert.Equal(.5f, dots[3].Opacity);
        motion.Sample(8.175, dots, lines, out _);
        Assert.Equal(.5f, dots[1].Opacity);
    }

    [Theory]
    [InlineData(DesktopLoadingAnimation.FiveDots)]
    [InlineData(DesktopLoadingAnimation.ClaudeNodes)]
    [InlineData(DesktopLoadingAnimation.ClaudeOrbit)]
    [InlineData(DesktopLoadingAnimation.ClaudeBreath)]
    [InlineData(DesktopLoadingAnimation.ClaudeQuiet)]
    [InlineData(DesktopLoadingAnimation.Gemini)]
    public void SkippedFramesAndRepeatedCyclesUseTheSameAbsoluteTimeline(DesktopLoadingAnimation animation)
    {
        var motion = DesktopLoadingMotion.For(animation);
        Span<DesktopLoadingDotFrame> first = stackalloc DesktopLoadingDotFrame[5];
        Span<DesktopLoadingDotFrame> later = stackalloc DesktopLoadingDotFrame[5];
        Span<DesktopLoadingLineFrame> lines = stackalloc DesktopLoadingLineFrame[4];
        // Quiet 的初始错峰已结束，后续保持同一周期。
        var time = motion.Period + .71;
        motion.Sample(time, first, lines, out _);
        motion.Sample(time + motion.Period * 100, later, lines, out _);
        for (var index = 0; index < motion.DotCount; index++)
        {
            Assert.InRange(Vector2.Distance(first[index].Center, later[index].Center), 0, .0002);
            Assert.InRange(Vector2.Distance(first[index].Radius, later[index].Radius), 0, .0002);
            Assert.InRange(Math.Abs(first[index].Opacity - later[index].Opacity), 0, .0002);
        }
    }

    [Fact]
    public void ArcKeepsTheBuiltInTwoSecondGrowShrinkAndRotation()
    {
        var motion = DesktopLoadingMotion.For(DesktopLoadingAnimation.Arc);
        motion.Sample(.5, [], [], out var growing);
        Assert.Equal(new DesktopLoadingArcFrame(0, .25f, 225), growing);
        motion.Sample(1.5, [], [], out var shrinking);
        Assert.Equal(new DesktopLoadingArcFrame(.25f, .5f, 675), shrinking);
        motion.Sample(2.5, [], [], out var repeated);
        Assert.Equal(growing, repeated);
    }

    [Fact]
    public void StillVariantsKeepVisibleDotsWithoutMotion()
    {
        Span<DesktopLoadingDotFrame> dots = stackalloc DesktopLoadingDotFrame[5];
        foreach (var animation in Enum.GetValues<DesktopLoadingAnimation>())
        {
            var motion = DesktopLoadingMotion.For(animation);
            motion.SampleStill(dots, out var arc);
            var count = 0;
            foreach (var dot in dots)
                if (dot.Opacity > 0) count++;
            Assert.Equal(animation == DesktopLoadingAnimation.Arc ? 0
                : animation == DesktopLoadingAnimation.FiveDots ? 5 : 3, count);
            Assert.True(arc.TrimEnd > arc.TrimStart);
        }
    }

    [Fact]
    public void SamplingAnActiveFrameDoesNotAllocateArraysOrAnimationObjects()
    {
        var motions = Enum.GetValues<DesktopLoadingAnimation>().Select(DesktopLoadingMotion.For).ToArray();
        Span<DesktopLoadingDotFrame> dots = stackalloc DesktopLoadingDotFrame[5];
        Span<DesktopLoadingLineFrame> lines = stackalloc DesktopLoadingLineFrame[4];
        foreach (var motion in motions) motion.Sample(.71, dots, lines, out _);
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var frame = 0; frame < 600; frame++)
            foreach (var motion in motions)
                motion.Sample(frame / 60d, dots, lines, out _);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }
}
