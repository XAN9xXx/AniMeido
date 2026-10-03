using System.Buffers.Binary;
using System.Text;
using AniMeido.Updates;
using Xunit;

namespace AniMeido.Updates.Tests;

public sealed class UpdatePackageExtractorTests
{
    private static readonly byte[] Bytes = Encoding.UTF8.GetBytes("content");

    [Fact]
    public void ValidArchiveWithDirectoryAndRequiredRootFile()
    {
        using var temp = new TemporaryDirectory();
        var zip = TestData.Zip(temp, ("AniMeido.App.exe", Bytes, 0), ("assets/", [], 0), ("assets/a.txt", Bytes, 0));
        var target = temp.PathFor("app-2.3.0");
        UpdatePackageExtractor.Extract(zip, target, ["AniMeido.App.exe"]);
        Assert.Equal(Bytes, File.ReadAllBytes(Path.Combine(target, "AniMeido.App.exe")));
        Assert.Equal(Bytes, File.ReadAllBytes(Path.Combine(target, "assets/a.txt")));
        Assert.False(Directory.Exists(target + ".staging"));
    }

    [Theory]
    [InlineData("/root")]
    [InlineData("//server/file")]
    [InlineData("C:/file")]
    [InlineData("a:b")]
    [InlineData("a\\b")]
    [InlineData("a\0b")]
    [InlineData(".")]
    [InlineData("..")]
    [InlineData("a/../b")]
    [InlineData("a/./b")]
    [InlineData("a//b")]
    [InlineData("a//")]
    [InlineData("a./b")]
    [InlineData("a /b")]
    [InlineData("CON")]
    [InlineData("con.txt")]
    [InlineData("PRN.exe")]
    [InlineData("AUX")]
    [InlineData("NUL.txt")]
    [InlineData("COM1")]
    [InlineData("COM9.dll")]
    [InlineData("LPT1")]
    [InlineData("LPT9.zip")]
    [InlineData("assets/CON.txt")]
    [InlineData("a?b")]
    [InlineData("a|b")]
    [InlineData("a<b")]
    public void InvalidEntryNamesAreRejected(string name)
    {
        using var temp = new TemporaryDirectory();
        var zip = TestData.Zip(temp, ("first.txt", Bytes, 0), (name, Bytes, 0));
        var target = temp.PathFor("app");
        TestData.Error(UpdateErrorKind.InvalidArchive, () => UpdatePackageExtractor.Extract(zip, target, []));
        Assert.False(Directory.Exists(target));
        Assert.False(Directory.Exists(target + ".staging"));
        Assert.False(File.Exists(temp.PathFor("b")));
    }

    [Fact]
    public void EmptyEntryNameRejected()
    {
        using var temp = new TemporaryDirectory();
        var zip = temp.PathFor("empty-name.zip");
        // 手工构造一个名称长度为零、内容为空的 ZIP；CreateEntry 本身不允许空名。
        using (var file = File.Create(zip))
        using (var writer = new BinaryWriter(file))
        {
            writer.Write(0x04034b50);
            writer.Write((ushort)20);
            writer.Write((ushort)0);
            writer.Write((ushort)0);
            writer.Write(0);
            writer.Write(0);
            writer.Write(0);
            writer.Write(0);
            writer.Write((ushort)0);
            writer.Write((ushort)0);
            writer.Write(0x02014b50);
            writer.Write((ushort)20);
            writer.Write((ushort)20);
            writer.Write((ushort)0);
            writer.Write((ushort)0);
            writer.Write(0);
            writer.Write(0);
            writer.Write(0);
            writer.Write(0);
            writer.Write((ushort)0);
            writer.Write((ushort)0);
            writer.Write((ushort)0);
            writer.Write((ushort)0);
            writer.Write((ushort)0);
            writer.Write(0);
            writer.Write(0);
            writer.Write(0x06054b50);
            writer.Write((ushort)0);
            writer.Write((ushort)0);
            writer.Write((ushort)1);
            writer.Write((ushort)1);
            writer.Write(46);
            writer.Write(30);
            writer.Write((ushort)0);
        }

        var target = temp.PathFor("app");
        TestData.Error(UpdateErrorKind.InvalidArchive, () => UpdatePackageExtractor.Extract(zip, target, []));
        Assert.False(Directory.Exists(target + ".staging"));
    }

    [Theory]
    [InlineData("file.txt", "FILE.TXT")]
    [InlineData("dir/", "DIR/")]
    [InlineData("dir/", "dir")]
    public void CaseInsensitiveDuplicatesRejected(string first, string second)
    {
        using var temp = new TemporaryDirectory();
        var zip = TestData.Zip(temp, (first, [], 0), (second, [], 0));
        var target = temp.PathFor("app");
        TestData.Error(UpdateErrorKind.InvalidArchive, () => UpdatePackageExtractor.Extract(zip, target, []));
        Assert.False(Directory.Exists(target));
        Assert.False(Directory.Exists(target + ".staging"));
    }

    [Fact]
    public void SymlinkRejected()
    {
        using var temp = new TemporaryDirectory();
        var zip = TestData.Zip(temp, ("link", Encoding.UTF8.GetBytes("../outside"), unchecked((int)0xA0000000)));
        var target = temp.PathFor("app");
        TestData.Error(UpdateErrorKind.InvalidArchive, () => UpdatePackageExtractor.Extract(zip, target, []));
        Assert.False(Directory.Exists(target + ".staging"));
    }

    [Fact]
    public void ActualBytesEnforceLimitEvenWhenHeaderLies()
    {
        using var temp = new TemporaryDirectory();
        var zip = TestData.Zip(temp, ("bomb", new byte[10000], 0));
        var data = File.ReadAllBytes(zip);
        // 本地头和中央目录都虚报 20000 字节，实际 DEFLATE 解压出 10000 字节。
        // 安全边界仍是实际读取/写入计数，而不是头中声明的数值。
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(22, 4), 20000);
        for (var i = 0; i < data.Length - 46; i++)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(i, 4)) == 0x02014b50)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(i + 24, 4), 20000);
                break;
            }
        }

        File.WriteAllBytes(zip, data);
        var target = temp.PathFor("app");
        TestData.Error(UpdateErrorKind.ArchiveLimitExceeded, () => UpdatePackageExtractor.Extract(zip, target, [],
            new UpdateExtractionLimits { MaxFileBytes = 10 }));
        Assert.False(Directory.Exists(target + ".staging"));
        Assert.False(Directory.Exists(target));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(20000)]
    public void FalseDeclaredLengthRejectedWithoutLeavingOutput(uint declaredLength)
    {
        using var temp = new TemporaryDirectory();
        var zip = TestData.Zip(temp, ("file", new byte[10000], 0));
        var data = File.ReadAllBytes(zip);
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(22, 4), declaredLength);
        for (var i = 0; i < data.Length - 46; i++)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(i, 4)) == 0x02014b50)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(i + 24, 4), declaredLength);
                break;
            }
        }

        File.WriteAllBytes(zip, data);
        var target = temp.PathFor("app");
        TestData.Error(UpdateErrorKind.InvalidArchive, () => UpdatePackageExtractor.Extract(zip, target, []));
        Assert.False(Directory.Exists(target));
        Assert.False(Directory.Exists(target + ".staging"));
    }

    [Fact]
    public void CorruptCrcRejected()
    {
        using var temp = new TemporaryDirectory();
        var zip = TestData.Zip(temp, ("file", Bytes, 0));
        var data = File.ReadAllBytes(zip);
        for (var i = 0; i < data.Length - 46; i++)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(i, 4)) == 0x02014b50)
            {
                data[i + 16] ^= 1;
                break;
            }
        }

        File.WriteAllBytes(zip, data);
        var target = temp.PathFor("app");
        TestData.Error(UpdateErrorKind.InvalidArchive, () => UpdatePackageExtractor.Extract(zip, target, []));
        Assert.False(Directory.Exists(target + ".staging"));
    }

    [Fact]
    public void TotalLimitAndEntryCountEnforced()
    {
        using var temp = new TemporaryDirectory();
        var zip = TestData.Zip(temp, ("one", new byte[6], 0), ("two", new byte[6], 0));
        var target = temp.PathFor("app");
        TestData.Error(UpdateErrorKind.ArchiveLimitExceeded, () => UpdatePackageExtractor.Extract(zip, target, [],
            new UpdateExtractionLimits { MaxTotalBytes = 10 }));
        Assert.False(Directory.Exists(target + ".staging"));
        TestData.Error(UpdateErrorKind.ArchiveLimitExceeded, () => UpdatePackageExtractor.Extract(zip, target, [],
            new UpdateExtractionLimits { MaxEntries = 1 }));
        Assert.False(Directory.Exists(target + ".staging"));
    }

    [Fact]
    public void MissingRequiredFileCleansStaging()
    {
        using var temp = new TemporaryDirectory();
        var zip = TestData.Zip(temp, ("nested/AniMeido.App.exe", Bytes, 0));
        var target = temp.PathFor("app");
        TestData.Error(UpdateErrorKind.RequiredFileMissing,
            () => UpdatePackageExtractor.Extract(zip, target, ["AniMeido.App.exe"]));
        Assert.False(Directory.Exists(target));
        Assert.False(Directory.Exists(target + ".staging"));
    }

    [Fact]
    public void ExistingTargetRefusedWithoutModification()
    {
        using var temp = new TemporaryDirectory();
        var zip = TestData.Zip(temp, ("file", Bytes, 0));
        var target = temp.PathFor("app");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "sentinel"), "keep");
        TestData.Error(UpdateErrorKind.TargetExists, () => UpdatePackageExtractor.Extract(zip, target, []));
        Assert.Equal("keep", File.ReadAllText(Path.Combine(target, "sentinel")));
    }

    [Fact]
    public void StaleStagingDeletedBeforeFreshExtraction()
    {
        using var temp = new TemporaryDirectory();
        var zip = TestData.Zip(temp, ("file", Bytes, 0));
        var target = temp.PathFor("app");
        Directory.CreateDirectory(target + ".staging");
        File.WriteAllText(Path.Combine(target + ".staging", "stale"), "stale");
        UpdatePackageExtractor.Extract(zip, target, ["file"]);
        Assert.False(File.Exists(Path.Combine(target, "stale")));
        Assert.Equal(Bytes, File.ReadAllBytes(Path.Combine(target, "file")));
    }

    [Theory]
    [InlineData("../file")]
    [InlineData("nested/file")]
    [InlineData("file/")]
    [InlineData("")]
    public void RequiredFilesMustBeRootNames(string required)
    {
        using var temp = new TemporaryDirectory();
        var zip = TestData.Zip(temp, ("file", Bytes, 0));
        TestData.Error(UpdateErrorKind.InvalidArchive,
            () => UpdatePackageExtractor.Extract(zip, temp.PathFor("app"), [required]));
    }

    [Fact]
    public void InputInsideStagingRefusedBeforeDeletion()
    {
        using var temp = new TemporaryDirectory();
        var target = temp.PathFor("app");
        Directory.CreateDirectory(target + ".staging");
        var input = Path.Combine(target + ".staging", "input.zip");
        File.WriteAllText(input, "keep");
        TestData.Error(UpdateErrorKind.InvalidArchive, () => UpdatePackageExtractor.Extract(input, target, []));
        Assert.Equal("keep", File.ReadAllText(input));
    }

    [Fact]
    public void InvalidZipCleansStaging()
    {
        using var temp = new TemporaryDirectory();
        var zip = temp.PathFor("bad.zip");
        File.WriteAllText(zip, "not a zip");
        var target = temp.PathFor("app");
        TestData.Error(UpdateErrorKind.InvalidArchive, () => UpdatePackageExtractor.Extract(zip, target, []));
        Assert.False(Directory.Exists(target + ".staging"));
    }
}
