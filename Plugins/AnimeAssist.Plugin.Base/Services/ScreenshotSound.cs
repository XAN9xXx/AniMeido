using System.Runtime.InteropServices;

namespace AniMeido.Plugin.Base.Services;

internal static class ScreenshotSound
{
    private const uint SndMemory = 0x0004;
    private const uint SndAsync = 0x0001;
    private static readonly byte[] Wave = CreateWave();

    public static void Play() => _ = PlaySound(
        Wave,
        0,
        SndMemory | SndAsync);

    private static byte[] CreateWave()
    {
        const int sampleRate = 22050;
        const int durationMilliseconds = 120;
        var sampleCount = sampleRate * durationMilliseconds / 1000;
        var dataSize = sampleCount * sizeof(short);
        using var stream = new MemoryStream(44 + dataSize);
        using var writer = new BinaryWriter(stream);
        writer.Write("RIFF"u8);
        writer.Write(36 + dataSize);
        writer.Write("WAVEfmt "u8);
        writer.Write(16);
        writer.Write((short)1);
        writer.Write((short)1);
        writer.Write(sampleRate);
        writer.Write(sampleRate * sizeof(short));
        writer.Write((short)sizeof(short));
        writer.Write((short)16);
        writer.Write("data"u8);
        writer.Write(dataSize);
        for (var index = 0; index < sampleCount; index++)
        {
            var envelope = 1d - (double)index / sampleCount;
            var frequency = 900d + 500d * index / sampleCount;
            var sample = Math.Sin(
                2 * Math.PI * frequency * index / sampleRate);
            writer.Write((short)(sample * envelope * short.MaxValue * 0.22));
        }

        return stream.ToArray();
    }

    [DllImport("winmm.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PlaySound(
        byte[] sound,
        nint module,
        uint flags);
}
