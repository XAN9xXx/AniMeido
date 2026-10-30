namespace AniMeido.Tests;

internal sealed class CalendarTestTime(DateOnly today, TimeSpan? localOffset = null) : TimeProvider
{
    private readonly TimeZoneInfo _zone = localOffset is { } offset
        ? TimeZoneInfo.CreateCustomTimeZone("calendar-fixture", offset, "calendar-fixture", "calendar-fixture")
        : TimeZoneInfo.Utc;

    internal DateOnly Today { get; set; } = today;

    public override TimeZoneInfo LocalTimeZone => _zone;

    public override DateTimeOffset GetUtcNow()
        => new DateTimeOffset(Today.ToDateTime(TimeOnly.MinValue), _zone.BaseUtcOffset).ToUniversalTime();
}
