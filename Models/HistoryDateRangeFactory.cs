namespace FindHistory.Models;

public static class HistoryDateRangeFactory
{
    public static HistoryDateRange CreateLocalCalendarRange(
        DateTime startLocal,
        DateTime endLocal,
        string label,
        TimeZoneInfo? timeZone = null)
    {
        var start = DateTime.SpecifyKind(startLocal, DateTimeKind.Unspecified);
        var end = DateTime.SpecifyKind(endLocal, DateTimeKind.Unspecified);
        if (end <= start)
        {
            throw new ArgumentOutOfRangeException(nameof(endLocal), "종료 시각은 시작 시각보다 늦어야 합니다.");
        }

        var zone = timeZone ?? TimeZoneInfo.Local;
        var startUtc = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(start, zone));
        var endUtc = new DateTimeOffset(TimeZoneInfo.ConvertTimeToUtc(end, zone));
        return new HistoryDateRange(startUtc, endUtc, label);
    }
}
