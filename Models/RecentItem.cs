namespace FindHistory.Models;

public sealed record RecentItemCandidate(
    string TargetPath,
    string DisplayName,
    string Extension,
    string ItemKind,
    string SourceLinkPath,
    DateTimeOffset LinkWriteTime,
    bool Exists);

public sealed record RecentItem(
    long Id,
    string TargetPath,
    string DisplayName,
    string Extension,
    string ItemKind,
    string SourceLinkPath,
    DateTimeOffset FirstSeen,
    DateTimeOffset LastSeen,
    int OpenCount,
    bool Exists,
    bool IsEstimatedHistory = false)
{
    public string LastSeenText => LastSeen.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
    public string CountText => $"{OpenCount:N0}회";
    public string ExistsText => Exists ? string.Empty : "찾을 수 없음";
}

public sealed record HistoryStats(long UniqueItems, long TotalOpenCount);

public sealed record SearchSnapshot(
    IReadOnlyList<RecentItem> Items,
    HistoryStats Stats);

public sealed record HistoryDateRange(
    DateTimeOffset StartUtc,
    DateTimeOffset EndUtc,
    string Label);

public sealed record DateRangeOption(
    string Label,
    int? CalendarDayCount = null,
    bool IsSpecificDate = false)
{
    public override string ToString() => Label;
}
