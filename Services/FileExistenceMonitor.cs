namespace FindHistory.Services;

public sealed class FileExistenceMonitor(RecentDatabase database, AppLogService log) : IAsyncDisposable
{
    private readonly CancellationTokenSource _stop = new();
    private readonly object _lifecycleLock = new();
    private Task? _worker;
    private Task? _disposeTask;
    public event EventHandler? HistoryChanged;

    public void Start()
    {
        lock (_lifecycleLock)
        {
            ObjectDisposedException.ThrowIf(_disposeTask is not null, this);
            _worker ??= Task.Run(RunAsync);
        }
    }

    private async Task RunAsync()
    {
        long cursor = 0;
        string? path = null;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(30));
        try
        {
            do
            {
                _stop.Token.ThrowIfCancellationRequested();
                try
                {
                    if (!string.Equals(path, database.DatabasePath, StringComparison.OrdinalIgnoreCase)) cursor = 0;
                    var result = await database.RefreshExistenceBatchAsync(cursor, cancellationToken: _stop.Token);
                    path = result.DatabasePath;
                    cursor = result.NextId;
                    if (result.ChangedItems > 0) HistoryChanged?.Invoke(this, EventArgs.Empty);
                }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested) { break; }
                catch (Exception ex) { log.Error("Background file existence refresh failed.", ex); }
            } while (await timer.WaitForNextTickAsync(_stop.Token));
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
    }

    public ValueTask DisposeAsync()
    {
        lock (_lifecycleLock)
        {
            _disposeTask ??= StopAsync();
            return new ValueTask(_disposeTask);
        }
    }

    private async Task StopAsync()
    {
        _stop.Cancel();
        if (_worker is not null) await _worker.ConfigureAwait(false);
        _stop.Dispose();
    }
}
