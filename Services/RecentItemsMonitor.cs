using System.IO;

namespace FindHistory.Services;

public sealed class RecentItemsMonitor : IDisposable
{
    private readonly RecentDatabase _database;
    private readonly ShortcutResolver _resolver;
    private readonly string _recentFolder;
    private readonly SemaphoreSlim _scanGate = new(1, 1);
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
            var count = 0;
            foreach (var path in Directory.EnumerateFiles(_recentFolder, "*.*", SearchOption.TopDirectoryOnly)
                         .Where(IsSupportedShortcut))
            {
                if (await CaptureAsync(path))
                {
                    count++;
                }
            }
            HistoryChanged?.Invoke(this, EventArgs.Empty);
            return count;
        }
        finally
        {
            _scanGate.Release();
        }
    }

    private async void OnShortcutChanged(object sender, FileSystemEventArgs e)
    {
        if (!IsSupportedShortcut(e.FullPath))
        {
            return;
        }

        try
        {
            await Task.Delay(250);
            if (await CaptureAsync(e.FullPath))
            {
                HistoryChanged?.Invoke(this, EventArgs.Empty);
            }
        }
        catch (Exception ex)
        {
            MonitorError?.Invoke(this, ex.Message);
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
        _scanGate.Dispose();
    }
}
