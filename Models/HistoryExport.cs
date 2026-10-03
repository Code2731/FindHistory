namespace FindHistory.Models;

public sealed record HistoryExportItem(
    long Id,
    string TargetPath,
    string DisplayName,
    string Extension,
    string ItemKind,
    string SourceLinkPath,
    DateTimeOffset FirstSeenUtc,
    DateTimeOffset LastSeenUtc,
    DateTimeOffset LastLinkWriteUtc,
    int OpenCount,
    bool Exists,
    IReadOnlyList<HistoryExportEvent> Events);

public sealed record HistoryExportEvent(
    DateTimeOffset OpenedUtc,
    string SourceLinkPath,
    bool IsEstimated);
