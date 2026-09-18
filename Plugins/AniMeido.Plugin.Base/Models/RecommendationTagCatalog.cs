namespace AniMeido.Plugin.Base.Models
{
    /// <summary>
    /// 推荐页引导选择的题材标签，也用来从 Bangumi 标签里挑出放送日历今日一抽展示的题材。
    /// </summary>
    internal static class RecommendationTagCatalog
    {
        public static IReadOnlyList<string> Tags { get; } =
        [
            "科幻", "奇幻", "恋爱", "日常", "喜剧", "动作",
            "悬疑", "治愈", "校园", "音乐", "运动", "冒险",
            "机器人", "青春", "历史", "推理", "战斗", "魔法",
            "家庭", "职场", "旅行", "美食", "美术", "萌系",
            "偶像", "公路片", "时空穿越", "超能力", "游戏", "社会",
            "剧情", "原创", "漫画改", "小说改", "群像", "成长",
        ];
    }
}
