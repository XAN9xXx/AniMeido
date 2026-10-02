namespace AniMeido.Tests;

/// <summary>共享放送日历时光机会话的测试彼此顺序执行，其他测试集合仍可并行。</summary>
[CollectionDefinition(CurrentSeasonSessionCollection.Name)]
public sealed class CurrentSeasonSessionCollection
{
    public const string Name = "CurrentSeasonViewModel session";
}
