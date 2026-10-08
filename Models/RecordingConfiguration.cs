namespace FindHistory.Models;

public sealed record RecordingConfiguration(bool Paused, IReadOnlyList<string> ExcludedFolders,
    DateTimeOffset? ResumeAfterUtc);
