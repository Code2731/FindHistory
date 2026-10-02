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

public sealed record HistoryFilters(string Extension = "", bool? Exists = null, string Folder = "");
public sealed record ExistenceOption(string Label, bool? Exists)
{
    public override string ToString() => Label;
}
public sealed record FilterChip(string Key, string Label);

public sealed record SearchSnapshot(
    IReadOnlyList<RecentItem> Items,
    HistoryStats Stats);

public sealed record DailyActivity(
    DateTime Date,
    int OpenCount,
    bool ContainsEstimated);

public sealed record ActivityDay(
    DateTime Date,
    int OpenCount,
    int Level,
    bool ContainsEstimated,
    bool IsToday,
    bool IsSelected,
    bool CanSelect)
{
    public string ToolTipText => !CanSelect
        ? Date.ToString("yyyy-MM-dd")
        : $"{Date:yyyy-MM-dd} · {OpenCount:N0}회" +
          (ContainsEstimated ? " · 일부 추정" : string.Empty);
}

public sealed record ActivityWeek(IReadOnlyList<ActivityDay> Days);

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
