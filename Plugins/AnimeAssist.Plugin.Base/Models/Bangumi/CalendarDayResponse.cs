namespace AniMeido.Plugin.Base.Models.Bangumi
{
    /// <summary>
    /// 某一天的日历数据，包含星期几信息和当天的番剧条目列表。
    /// </summary>
    /// <remarks>
    /// 参数列表: WeekdayInfo Weekday, List'CalendarItem' Items
    /// </remarks>
    /// <param name="Weekday">星期几信息。</param>
    /// <param name="Items">番剧条目列表。</param>
    internal record CalendarDayResponse(WeekdayInfo Weekday, List<CalendarItem> Items);

}
