using System.Globalization;
using AniMeido.Plugin.Base.Services;
using ToolGood.Words.Pinyin;

namespace AniMeido.Tests;

public sealed class TitleSortComparerTests
{
    internal static readonly string[] MixedTitles =
    [
        "86 不存在的战区",
        "阿尔卑斯",
        "BanG Dream!",
        "鬼灭之刃",
        "Overlord",
        "Re：从零开始",
        "银魂",
        "葬送的芙莉莲",
        "カタカナ",
    ];

    [Fact]
    public void Sort_MixesChinesePinyinAndEnglish()
        => Assert.Equal(MixedTitles, Sort(MixedTitles.Reverse()));

    [Theory]
    [InlineData("重新", "ChongXin")]
    [InlineData("重庆", "ChongQing")]
    [InlineData("重量", "ZhongLiang")]
    [InlineData("银行", "YinHang")]
    [InlineData("行走", "XingZou")]
    public void Compare_UsesVerifiedPhrasePronunciations(string title, string pinyin)
    {
        Assert.Equal(pinyin, WordsHelper.GetPinyin(title, tone: false));
        // 与其拼音的键相同，所以应由原始标题的序数比较打破平局。
        Assert.Equal(
            Math.Sign(StringComparer.Ordinal.Compare(title, pinyin.ToLowerInvariant())),
            Math.Sign(TitleSortComparer.Instance.Compare(title, pinyin.ToLowerInvariant())));
    }

    [Fact]
    public void Sort_UsesPhraseReadingsRatherThanSingleCharacterReadings()
    {
        Assert.Equal(
            new[] { "重庆", "重新", "Dora", "行走", "银行", "重量" },
            Sort(["重量", "银行", "Dora", "重新", "行走", "重庆"]));
    }

    [Fact]
    public void Sort_PutsDigitsFirstAndUsesOrdinalNotNaturalNumberOrder()
        => Assert.Equal(
            new[] { "10 番", "2 番", "８６ 番", "阿尔卑斯", "BanG Dream!" },
            Sort(["BanG Dream!", "８６ 番", "阿尔卑斯", "2 番", "10 番"]));

    [Fact]
    public void Sort_NormalizesFullWidthLatinLettersAndDigits()
    {
        Assert.Equal(
            new[] { "bana", "BanG Dream!", "ＢａｎＧ Dream！", "banz" },
            Sort(["banz", "ＢａｎＧ Dream！", "bana", "BanG Dream!"]));
        Assert.Equal(
            Math.Sign(StringComparer.Ordinal.Compare("86", "８６")),
            Math.Sign(TitleSortComparer.Instance.Compare("86", "８６")));
    }

    [Theory]
    [InlineData("ひらがな")]
    [InlineData("カタカナ")]
    [InlineData("ㇰ")]
    public void Sort_PutsEachKanaRangeAfterChineseAndEnglish(string kana)
        => Assert.Equal(
            new[] { "阿尔卑斯", "BanG Dream!", "葬送的芙莉莲", kana },
            Sort([kana, "葬送的芙莉莲", "BanG Dream!", "阿尔卑斯"]));

    [Fact]
    public void Sort_PutsOtherScriptsAfterKanaAndPreservesTheirLetters()
        => Assert.Equal(
            new[] { "Zulu", "カタカナ", "가나", "하나" },
            Sort(["하나", "カタカナ", "가나", "Zulu"]));

    [Fact]
    public void Compare_IgnoresPunctuationWhitespaceAndSymbolsInKeys()
    {
        const string decorated = " 【✨ B! a—n G!? Dream!! 】 ";
        const string plain = "BangDream";
        Assert.Equal(
            Math.Sign(StringComparer.Ordinal.Compare(decorated, plain)),
            Math.Sign(TitleSortComparer.Instance.Compare(decorated, plain)));
        Assert.True(TitleSortComparer.Instance.Compare(decorated, "bana") > 0);
        Assert.True(TitleSortComparer.Instance.Compare(decorated, "banz") < 0);
        Assert.True(TitleSortComparer.Instance.Compare(" 【✨ 阿尔卑斯 】 ", "BanG Dream!") < 0);
    }

    public static TheoryData<string[]> EqualKeyTitles => new()
    {
        new[] { "BanG Dream!", "BANGDREAM", "bangdream", "ＢＡＮＧＤＲＥＡＭ", "BanG-Dream" },
        new[] { "银魂", "yinhun", "YinHun" },
    };

    [Theory]
    [MemberData(nameof(EqualKeyTitles))]
    public void Sort_EqualKeysUseRawOrdinalOrderRegardlessOfInput(string[] titles)
    {
        var expected = titles.Order(StringComparer.Ordinal).ToArray();
        Assert.Equal(expected, Sort(titles));
        Assert.Equal(expected, Sort(titles.Reverse()));
        Assert.Equal(expected, Sort(titles.Skip(1).Append(titles[0])));
    }

    [Theory]
    [InlineData("カ タカナ", "カタカナ")]
    [InlineData("가 나", "가나")]
    [InlineData("!", " ")]
    public void Compare_KanaAndOtherEqualOrEmptyKeysUseCultureThenRawOrdinal(string left, string right)
    {
        var expected = StringComparer.Create(CultureInfo.GetCultureInfo("zh-CN"), ignoreCase: true)
            .Compare(left, right);
        if (expected == 0)
            expected = StringComparer.Ordinal.Compare(left, right);

        Assert.Equal(Math.Sign(expected), Math.Sign(TitleSortComparer.Instance.Compare(left, right)));
    }

    [Fact]
    public void Compare_HandlesNullEmptyAndIdenticalTitles()
    {
        Assert.Equal(0, TitleSortComparer.Instance.Compare(null, null));
        Assert.True(TitleSortComparer.Instance.Compare(null, "") < 0);
        Assert.True(TitleSortComparer.Instance.Compare("", null) > 0);
        Assert.Equal(0, TitleSortComparer.Instance.Compare("银魂", "银魂"));
        Assert.True(TitleSortComparer.Instance.Compare("Zulu", "") < 0);
    }

    [Fact]
    public void Sort_IsDeterministicUnderConcurrentRepeatedUse()
        => Parallel.For(0, 100, _ => Assert.Equal(MixedTitles, Sort(MixedTitles.Reverse())));

    private static string[] Sort(IEnumerable<string> titles)
        => titles.Order(TitleSortComparer.Instance).ToArray();
}
