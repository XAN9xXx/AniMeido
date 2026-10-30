using AniMeido.Plugin.Base.Services;
using System.Buffers.Binary;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace AniMeido.Tests;

public sealed class ScreenshotSoundTests
{
    [Fact]
    [Trait("Category", "Mechanism")]
    public void WaveCreationFillsAndReturnsThePermanentlyPinnedBuffer()
    {
        var source = ReadSource();
        var creation = source[source.IndexOf(
            "private static byte[] CreateWave()", StringComparison.Ordinal)..];
        var allocation = Regex.Match(
            creation,
            @"var\s+(?<buffer>\w+)\s*=\s*GC\.AllocateArray<byte>\(\s*44\s*\+\s*dataSize\s*,\s*pinned:\s*true\s*\)",
            RegexOptions.CultureInvariant,
            TimeSpan.FromSeconds(1));

        Assert.True(allocation.Success, "The WAV must be allocated as a pinned array.");
        var buffer = allocation.Groups["buffer"].Value;
        Assert.Contains($"new MemoryStream({buffer})", creation, StringComparison.Ordinal);
        Assert.Contains($"return {buffer};", creation, StringComparison.Ordinal);
        Assert.Contains("private static readonly byte[] Wave = CreateWave();", source,
            StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Category", "Mechanism")]
    public void AsyncPlaybackPassesThePersistentWaveBufferAddressToNativeCode()
    {
        var native = typeof(ScreenshotSound).GetMethod(
            "PlaySound", BindingFlags.Static | BindingFlags.NonPublic)!;
        Assert.Equal(typeof(nint), native.GetParameters()[0].ParameterType);
        Assert.Equal("winmm.dll", native.GetCustomAttribute<DllImportAttribute>()!.Value);

        var source = ReadSource();
        var first = source.IndexOf("public static void Play()", StringComparison.Ordinal);
        var last = source.IndexOf("private static byte[] CreateWave()", StringComparison.Ordinal);
        Assert.True(first >= 0 && last > first);
        Assert.Matches(
            @"PlaySound\(\s*Marshal\.UnsafeAddrOfPinnedArrayElement\(\s*Wave\s*,\s*0\s*\)\s*,\s*0\s*,\s*SndMemory\s*\|\s*SndAsync\s*\)",
            source[first..last]);
    }

    [Fact]
    public void WaveCreationKeepsTheOriginal120MillisecondMonoPcmFormat()
    {
        var wave = Assert.IsType<byte[]>(typeof(ScreenshotSound)
            .GetMethod("CreateWave", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, null));

        Assert.Equal(5336, wave.Length);
        Assert.Equal("RIFF", Encoding.ASCII.GetString(wave, 0, 4));
        Assert.Equal(5328u, BinaryPrimitives.ReadUInt32LittleEndian(wave.AsSpan(4, 4)));
        Assert.Equal("WAVEfmt ", Encoding.ASCII.GetString(wave, 8, 8));
        Assert.Equal(16u, BinaryPrimitives.ReadUInt32LittleEndian(wave.AsSpan(16, 4)));
        Assert.Equal((ushort)1, BinaryPrimitives.ReadUInt16LittleEndian(wave.AsSpan(20, 2)));
        Assert.Equal((ushort)1, BinaryPrimitives.ReadUInt16LittleEndian(wave.AsSpan(22, 2)));
        Assert.Equal(22050u, BinaryPrimitives.ReadUInt32LittleEndian(wave.AsSpan(24, 4)));
        var byteRate = BinaryPrimitives.ReadUInt32LittleEndian(wave.AsSpan(28, 4));
        Assert.Equal(44100u, byteRate);
        Assert.Equal((ushort)2, BinaryPrimitives.ReadUInt16LittleEndian(wave.AsSpan(32, 2)));
        Assert.Equal((ushort)16, BinaryPrimitives.ReadUInt16LittleEndian(wave.AsSpan(34, 2)));
        Assert.Equal("data", Encoding.ASCII.GetString(wave, 36, 4));
        var dataSize = BinaryPrimitives.ReadUInt32LittleEndian(wave.AsSpan(40, 4));
        Assert.Equal(5292u, dataSize);
        Assert.Equal(120u, dataSize * 1000 / byteRate);
        Assert.Equal((short)0, BinaryPrimitives.ReadInt16LittleEndian(wave.AsSpan(44, 2)));
        Assert.NotEqual((short)0, BinaryPrimitives.ReadInt16LittleEndian(wave.AsSpan(46, 2)));
    }

    private static string ReadSource()
        => File.ReadAllText(Path.Combine(
            AppDataRootsTests.RepositoryRoot,
            "Plugins", "AniMeido.Plugin.Base", "Services", "ScreenshotSound.cs"));
}
