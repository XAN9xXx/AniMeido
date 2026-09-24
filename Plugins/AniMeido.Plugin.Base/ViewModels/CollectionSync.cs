using System.Collections.ObjectModel;

namespace AniMeido.Plugin.Base.ViewModels;

/// <summary>把列表原地改成目标内容，未变化的项保持同一对象，列表控件不会重置滚动位置。</summary>
public static class CollectionSync
{
    /// <summary>
    /// 按 <paramref name="key"/> 对齐：删除多余项、插入或移动缺少的项、替换内容变化的项。
    /// 任一侧存在重复键时无法对齐，返回 false，由调用方整体替换。
    /// </summary>
    public static bool SyncInPlace<T, TKey>(
        ObservableCollection<T> current,
        IReadOnlyList<T> target,
        Func<T, TKey> key)
        where TKey : notnull
    {
        var targetKeys = target.Select(key).ToHashSet();
        if (targetKeys.Count != target.Count
            || current.Select(key).Distinct().Count() != current.Count)
        {
            return false;
        }

        for (var index = current.Count - 1; index >= 0; index--)
        {
            if (!targetKeys.Contains(key(current[index])))
                current.RemoveAt(index);
        }

        var comparer = EqualityComparer<TKey>.Default;
        for (var index = 0; index < target.Count; index++)
        {
            var wanted = target[index];
            var wantedKey = key(wanted);
            if (index >= current.Count || !comparer.Equals(key(current[index]), wantedKey))
            {
                var found = -1;
                for (var later = index + 1; later < current.Count; later++)
                {
                    if (comparer.Equals(key(current[later]), wantedKey))
                    {
                        found = later;
                        break;
                    }
                }

                if (found < 0)
                {
                    current.Insert(index, wanted);
                    continue;
                }

                current.Move(found, index);
            }

            if (!Equals(current[index], wanted))
                current[index] = wanted;
        }

        return true;
    }
}
