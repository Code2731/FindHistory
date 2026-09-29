namespace FindHistory.Models;

public sealed record MonitorDiagnostics(
    bool IsStarted,
    bool IsStopping,
    bool IsWatcherActive,
    string RecentFolder,
    DateTimeOffset? StartedUtc,
    DateTimeOffset? LastScanCompletedUtc,
    TimeSpan? LastScanDuration,
    int LastScanItemCount,
    DateTimeOffset? LastCaptureUtc,
    long SessionCaptureCount,
    int WatcherRecoveryCount,
    DateTimeOffset? LastErrorUtc,
    string? LastErrorMessage,
    int PendingTaskCount);

public sealed record DatabaseDiagnostics(
    long UniqueItems,
    long TotalOpenCount,
    long StoredEvents,
    long EstimatedEvents);
