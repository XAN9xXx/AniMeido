using System.Reflection;
using AniMeido.Updates;
using Xunit;

namespace AniMeido.Updates.Tests;

public sealed class SemanticVersionTests
{
    [Fact]
    public void Section11CompleteOrdering()
    {
        string[] chain =
        [
            "1.0.0-alpha", "1.0.0-alpha.1", "1.0.0-alpha.beta", "1.0.0-beta",
            "1.0.0-beta.2", "1.0.0-beta.11", "1.0.0-rc.1", "1.0.0", "2.0.0", "2.1.0", "2.1.1"
        ];
        for (var i = 0; i < chain.Length; i++)
        {
            for (var j = 0; j < chain.Length; j++)
            {
                var left = SemanticVersion.Parse(chain[i]);
                var right = SemanticVersion.Parse(chain[j]);
                Assert.Equal(Math.Sign(i - j), Math.Sign(left.CompareTo(right)));
                Assert.Equal(i == j, left.Equals(right));
            }
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("01.2.3")]
    [InlineData("1.02.3")]
    [InlineData("1.2.03")]
    [InlineData("1.2.3-01")]
    [InlineData("1.2.3-alpha.01")]
    [InlineData("1..3")]
    [InlineData("1.2")]
    [InlineData("1.2.3.4")]
    [InlineData("v1.2.3")]
    [InlineData("1.2.3-")]
    [InlineData("1.2.3+")]
    [InlineData("1.2.3-a..b")]
    [InlineData("1.2.3+a..b")]
    [InlineData("1.2.3+a+b")]
    [InlineData("1.2.3-alpha_beta")]
    [InlineData("１.2.3")]
    [InlineData("1.2.3-中文")]
    [InlineData("1.2.3+é")]
    [InlineData(" 1.2.3")]
    [InlineData("1.2.3\n")]
    public void InvalidInput(string? value)
    {
        Assert.False(SemanticVersion.TryParse(value, out var result));
        Assert.Null(result);
        TestData.Error(UpdateErrorKind.InvalidVersion, () => SemanticVersion.Parse(value!));
    }

    [Theory]
    [InlineData("1.2.3", false)]
    [InlineData("1.2.3+012.abc", false)]
    [InlineData("1.2.3-alpha-1+deadbeef", true)]
    [InlineData("999999999999999999999999999999.0.0-999999999999999999999999999999", true)]
    [InlineData("0.0.0-0", true)]
    [InlineData("1.2.3--", true)]
    public void ValidInputRetainsOriginal(string value, bool prerelease)
    {
        Assert.True(SemanticVersion.TryParse(value, out var version));
        Assert.Equal(value, version!.ToString());
        Assert.Equal(prerelease, version.IsPrerelease);
    }

    [Fact]
    public void BuildMetadataDoesNotAffectEqualityOrHash()
    {
        var left = SemanticVersion.Parse("1.2.3-alpha+one");
        var right = SemanticVersion.Parse("1.2.3-alpha+two");
        Assert.Equal(left, right);
        Assert.Equal(left.GetHashCode(), right.GetHashCode());
        Assert.Equal(0, left.CompareTo(right));
        Assert.True(left == right);
        Assert.False(left != right);
        Assert.False(left == null);
        Assert.True((SemanticVersion?)null == null);
        Assert.Equal(1, left.CompareTo(null));
        Assert.Throws<ArgumentException>(() => ((IComparable)left).CompareTo("1.2.3"));
    }

    [Fact]
    public void NumericIdentifiersDoNotOverflow()
    {
        Assert.True(SemanticVersion.Parse("1.0.0-9999999999999999999999")
            .CompareTo(SemanticVersion.Parse("1.0.0-10000000000000000000000")) < 0);
        Assert.True(SemanticVersion.Parse("9999999999999999999999.0.0")
            .CompareTo(SemanticVersion.Parse("10000000000000000000000.0.0")) < 0);
        Assert.True(SemanticVersion.Parse("1.0.0-9").CompareTo(SemanticVersion.Parse("1.0.0-a")) < 0);
        Assert.True(SemanticVersion.Parse("1.0.0-A").CompareTo(SemanticVersion.Parse("1.0.0-a")) < 0);
    }

    [Fact]
    public void AssemblyInformationalVersionIncludesSdkCommit()
    {
        // 测试程序集的 informational version 也由 SDK 生成，可含 +commit。
        var assembly = typeof(SemanticVersionTests).Assembly;
        var version = SemanticVersion.FromAssembly(assembly);
        var expected = assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()!.InformationalVersion;
        Assert.Equal(expected, version.ToString());
        Assert.Equal("2.3.0-preview.1+79d1534", SemanticVersion.Parse("2.3.0-preview.1+79d1534").ToString());
    }
}
