namespace FindHistory.Models;

public sealed record DatabaseStorageStatistics(
    string DatabasePath,
    long DatabaseBytes,
    long WalBytes,
    long SharedMemoryBytes,
    long AllocatedBytes,
    long FreeBytes)
{
    public long TotalFileBytes => DatabaseBytes + WalBytes + SharedMemoryBytes;
}
