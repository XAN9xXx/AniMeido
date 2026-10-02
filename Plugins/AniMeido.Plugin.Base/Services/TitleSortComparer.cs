using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using ToolGood.Words.Pinyin;

namespace AniMeido.Plugin.Base.Services;

/// <summary>数字开头按数值排在前面，中英按拼音混排，然后是假名和其他文字；相同键按原始标题排序。</summary>
internal sealed class TitleSortComparer : IComparer<string>
{
    public static TitleSortComparer Instance { get; } = new();

    private static readonly StringComparer CultureFallback =
        StringComparer.Create(CultureInfo.GetCultureInfo("zh-CN"), ignoreCase: true);

    private readonly ConcurrentDictionary<string, Lazy<SortKey>> _keys = new(StringComparer.Ordinal);

    private TitleSortComparer()
    {
    }

    public int Compare(string? x, string? y)
    {
        if (ReferenceEquals(x, y))
            return 0;
        if (x is null)
            return -1;
        if (y is null)
            return 1;

        var left = GetKey(x);
        var right = GetKey(y);
        var result = left.Group.CompareTo(right.Group);
        if (result != 0)
            return result;

        result = left.Group == 0
            ? CompareNumericKeys(left, right)
            : StringComparer.Ordinal.Compare(left.Value, right.Value);
        // 假名、韩文等保留的文字本身就是有效键；同键（包括无有效字符的空键）才文化兜底。
        if (result == 0 && left.Group >= 2)
            result = CultureFallback.Compare(x, y);

        return result != 0 ? result : StringComparer.Ordinal.Compare(x, y);
    }

    private static int CompareNumericKeys(SortKey left, SortKey right)
    {
        // 去掉前导零后先比位数，再比数字，等价于非负整数比较且没有整数溢出风险。
        var result = left.LeadingNumber.Length.CompareTo(right.LeadingNumber.Length);
        if (result == 0)
            result = StringComparer.Ordinal.Compare(left.LeadingNumber, right.LeadingNumber);
        if (result != 0)
            return result;

        return left.Value.AsSpan(left.NumberEnd).SequenceCompareTo(right.Value.AsSpan(right.NumberEnd));
    }

    private SortKey GetKey(string title)
        => _keys.GetOrAdd(title, static value => new Lazy<SortKey>(
            () => CreateKey(value), LazyThreadSafetyMode.ExecutionAndPublication)).Value;

    private static SortKey CreateKey(string title)
    {
        var normalized = title.Normalize(NormalizationForm.FormKC);
        var group = 3;
        var foundFirst = false;
        var hasChinese = false;
        foreach (var rune in normalized.EnumerateRunes())
        {
            hasChinese |= IsChinese(rune);
            if (foundFirst || !IsEffective(rune))
                continue;

            group = Rune.IsDigit(rune) ? 0
                : IsLatin(rune) || IsChinese(rune) ? 1
                : IsKana(rune) ? 2 : 3;
            foundFirst = true;
        }

        // 对整段规范化文字调用词组转换，不能逐字转换或预先删掉词组间的标点。
        // 只有真正比较含汉字的标题才调用库；取 Instance 或构造 ViewModel 不加载拼音数据。
        var converted = hasChinese ? PinyinConverter.Convert(normalized) : normalized;
        var key = new StringBuilder(converted.Length);
        foreach (var rune in converted.EnumerateRunes())
        {
            if (IsEffective(rune))
                key.Append(Rune.ToLowerInvariant(rune).ToString());
        }

        var value = key.ToString();
        if (group != 0)
            return new SortKey(group, value, "", 0);

        var leadingNumber = new StringBuilder();
        var numberEnd = 0;
        foreach (var rune in value.EnumerateRunes())
        {
            if (!Rune.IsDigit(rune))
                break;

            // 同时支持 NFKC 后的全角数字，以及其他 Unicode 十进制数字。
            leadingNumber.Append((char)('0' + (int)Rune.GetNumericValue(rune)));
            numberEnd += rune.Utf16SequenceLength;
        }

        return new SortKey(group, value, leadingNumber.ToString().TrimStart('0'), numberEnd);
    }

    private static bool IsEffective(Rune rune)
        => Rune.IsLetterOrDigit(rune)
            || (IsKana(rune) && Rune.GetUnicodeCategory(rune) is
                UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark);

    private static bool IsKana(Rune rune)
        => rune.Value is >= 0x3040 and <= 0x309F
            or >= 0x30A0 and <= 0x30FF
            or >= 0x31F0 and <= 0x31FF;

    private static bool IsLatin(Rune rune)
        => Rune.IsLetter(rune) && rune.Value is
            >= 0x0041 and <= 0x007A
            or >= 0x00C0 and <= 0x024F
            or >= 0x1E00 and <= 0x1EFF
            or >= 0x2C60 and <= 0x2C7F
            or >= 0xA720 and <= 0xA7FF
            or >= 0xAB30 and <= 0xAB6F
            or >= 0x10780 and <= 0x107BF
            or >= 0x1DF00 and <= 0x1DFFF;

    private static bool IsChinese(Rune rune)
        => rune.Value is >= 0x3400 and <= 0x4DBF
            or >= 0x4E00 and <= 0x9FFF
            or >= 0xF900 and <= 0xFAFF
            or >= 0x20000 and <= 0x2FA1F
            or >= 0x30000 and <= 0x323AF;

    private readonly record struct SortKey(int Group, string Value, string LeadingNumber, int NumberEnd);

    private static class PinyinConverter
    {
        private static readonly object SyncRoot = new();

        public static string Convert(string text)
        {
            // 不依赖第三方库对并发首次初始化及词组查询的线程安全保证。
            lock (SyncRoot)
                return WordsHelper.GetPinyin(text, tone: false);
        }
    }
}
