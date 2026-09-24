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
    bool Exists)
{
    public string LastSeenText => LastSeen.ToLocalTime().ToString("yyyy-MM-dd HH:mm");
    public string CountText => $"{OpenCount:N0}회";
    public string ExistsText => Exists ? string.Empty : "찾을 수 없음";
}

public sealed record HistoryStats(long UniqueItems, long TotalOpenCount);

public sealed record DateRangeOption(string Label, TimeSpan? Duration)
{
    public override string ToString() => Label;
}
