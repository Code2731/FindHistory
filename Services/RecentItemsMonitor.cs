using System.Collections.Concurrent;
using System.IO;
using FindHistory.Models;

namespace FindHistory.Services;

public sealed class RecentItemsMonitor : IDisposable
{
    private readonly RecentDatabase _database;
    private readonly ShortcutResolver _resolver;
    private readonly string _recentFolder;
    private readonly SemaphoreSlim _scanGate = new(1, 1);
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _pendingCaptures =
        new(StringComparer.OrdinalIgnoreCase);
    private FileSystemWatcher? _watcher;
    private bool _disposed;

    public event EventHandler? HistoryChanged;
    public event EventHandler<string>? MonitorError;

    public RecentItemsMonitor(RecentDatabase database, ShortcutResolver resolver)
    {
        _database = database;
        _resolver = resolver;
        _recentFolder = Environment.GetFolderPath(Environment.SpecialFolder.Recent);
    }

    public async Task StartAsync()
    {
        await ScanAsync();

        if (!Directory.Exists(_recentFolder))
        {
            return;
        }

        _watcher = new FileSystemWatcher(_recentFolder)
        {
            Filter = "*.*",
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.CreationTime,
            IncludeSubdirectories = false,
            EnableRaisingEvents = true
        };
        _watcher.Created += OnShortcutChanged;
        _watcher.Changed += OnShortcutChanged;
        _watcher.Renamed += OnShortcutChanged;
        _watcher.Error += (_, e) => MonitorError?.Invoke(this, e.GetException().Message);
    }

    public async Task<int> ScanAsync()
    {
        await _scanGate.WaitAsync();
        try
        {
            var items = await Task.Run(ResolveExistingItems);
            await _database.UpsertManyAsync(items);
            HistoryChanged?.Invoke(this, EventArgs.Empty);
            return items.Count;
        }
        finally
        {
            _scanGate.Release();
        }
    }

    private List<RecentItemCandidate> ResolveExistingItems()
    {
        var items = new List<RecentItemCandidate>();
        if (!Directory.Exists(_recentFolder))
        {
            return items;
        }

        foreach (var path in Directory.EnumerateFiles(_recentFolder, "*.*", SearchOption.TopDirectoryOnly)
                     .Where(IsSupportedShortcut))
        {
            var item = _resolver.Resolve(path);
            if (item is not null)
            {
                items.Add(item);
            }
        }
        return items;
    }

    private async void OnShortcutChanged(object sender, FileSystemEventArgs e)
    {
        if (!IsSupportedShortcut(e.FullPath))
        {
            return;
        }

        var path = e.FullPath;
        var cancellation = new CancellationTokenSource();
        if (_pendingCaptures.TryGetValue(path, out var previous))
        {
            previous.Cancel();
            previous.Dispose();
        }
        _pendingCaptures[path] = cancellation;

        try
        {
            await Task.Delay(250, cancellation.Token);
            if (await CaptureAsync(path))
            {
                HistoryChanged?.Invoke(this, EventArgs.Empty);
            }
        }
        catch (OperationCanceledException)
        {
            // 같은 파일에 더 최신 이벤트가 들어와 대체됨.
        }
        catch (Exception ex)
        {
            MonitorError?.Invoke(this, ex.Message);
        }
        finally
        {
            if (_pendingCaptures.TryRemove(new KeyValuePair<string, CancellationTokenSource>(path, cancellation)))
            {
                cancellation.Dispose();
            }
        }
    }

    private async Task<bool> CaptureAsync(string path)
    {
        var item = _resolver.Resolve(path);
        if (item is null)
        {
            return false;
        }

        await _database.UpsertAsync(item);
        return true;
    }

    private static bool IsSupportedShortcut(string path)
    {
        var extension = Path.GetExtension(path);
        return extension.Equals(".lnk", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".url", StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }
        _disposed = true;
        _watcher?.Dispose();
        foreach (var pending in _pendingCaptures.Values)
        {
            pending.Cancel();
            pending.Dispose();
        }
        _pendingCaptures.Clear();
        _scanGate.Dispose();
    }
}
