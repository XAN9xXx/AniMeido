namespace AniMeido.Plugin.Base.Models
{
    /// <summary>
    /// 可重复的打乱：同样的键和同一批 ID 得到的顺序永远相同，跨运行一致。
    /// 不用 Random 或字符串哈希，它们在不同运行之间结果不同。
    /// </summary>
    internal static class StableShuffle
    {
        /// <summary>按日期打乱（放送日历的今日一抽、今天页的今日主题）。</summary>
        public static IReadOnlyList<int> ByDate(IEnumerable<int> ids, DateOnly date)
            => ByKey(ids, (uint)date.DayNumber);

        /// <summary>按任意键打乱；键相同则顺序相同。</summary>
        public static IReadOnlyList<int> ByKey(IEnumerable<int> ids, uint key)
            => ids
                .Distinct()
                .OrderBy(id => Mix(((ulong)key << 32) | (uint)id))
                .ThenBy(id => id)
                .ToList();

        // SplitMix64 的混合步骤：输入相同则结果相同，且分布足够打散。
        private static ulong Mix(ulong value)
        {
            unchecked
            {
                value += 0x9E3779B97F4A7C15UL;
                value = (value ^ (value >> 30)) * 0xBF58476D1CE4E5B9UL;
                value = (value ^ (value >> 27)) * 0x94D049BB133111EBUL;
                return value ^ (value >> 31);
            }
        }
    }
}
