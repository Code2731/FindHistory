namespace FindHistory.Models;

public sealed record ExistenceRefreshResult(string DatabasePath, long NextId, int CheckedItems, int ChangedItems);
