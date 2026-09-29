using System.Collections.Concurrent;
using System.IO;
using FindHistory.Models;

namespace FindHistory.Services;

public sealed class RecentItemsMonitor : IDisposable, IAsyncDisposable
{
    private readonly RecentDatabase _database;
    private readonly ShortcutResolver _resolver;
    private readonly string _recentFolder;
    private readonly SemaphoreSlim _scanGate = new(1, 1);
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private readonly ConcurrentDictionary<string, CancellationTokenSource> _pendingCaptures =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<long, Task> _inFlightTasks = new();
    private readonly object _watcherLock = new();
    private readonly object _stopLock = new();
    private FileSystemWatcher? _watcher;
    private Task? _stopTask;
    private long _nextTaskId;
    private int _started;
    private int _recoveryScheduled;
    private int _stopping;
    private bool _resourcesDisposed;

    public event EventHandler? HistoryChanged;
    public event EventHandler<string>? MonitorError;

    public RecentItemsMonitor(
        RecentDatabase database,
        ShortcutResolver resolver,
        string? recentFolder = null)
    {
        _database = database;
        _resolver = resolver;
        _recentFolder = recentFolder ?? Environment.GetFolderPath(Environment.SpecialFolder.Recent);
    }

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfStopped();
        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            return;
        }

        // 감시를 먼저 켜고 전체 스캔을 수행해야 두 작업 사이에 생성된 바로가기를 놓치지 않는다.
        StartWatcherIfAvailable();
        await ScanAsync(cancellationToken, notifyChanges: false);
    }

    public async Task<int> ScanAsync(
        CancellationToken cancellationToken = default,
        bool notifyChanges = true)
    {
        ThrowIfStopped();
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken, _lifetimeCancellation.Token);
        var token = linkedCancellation.Token;

        await _scanGate.WaitAsync(token);
        try
        {
            var items = await Task.Run(() => ResolveExistingItems(token), token);
            await _database.UpsertManyAsync(items, token);
            if (notifyChanges)
            {
                HistoryChanged?.Invoke(this, EventArgs.Empty);
            }
            return items.Count;
        }
        finally
        {
            _scanGate.Release();
        }
    }

    private void StartWatcherIfAvailable()
    {
        if (!Directory.Exists(_recentFolder) || Volatile.Read(ref _stopping) != 0)
        {
            return;
        }

        var watcher = new FileSystemWatcher(_recentFolder)
        {
            Filter = "*.*",
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.CreationTime,
            IncludeSubdirectories = false
        };
        watcher.Created += OnShortcutChanged;
        watcher.Changed += OnShortcutChanged;
        watcher.Renamed += OnShortcutChanged;
        watcher.Error += OnWatcherError;

        lock (_watcherLock)
        {
            if (Volatile.Read(ref _stopping) != 0)
            {
                watcher.Dispose();
                return;
            }

            var previous = _watcher;
            _watcher = watcher;
            watcher.EnableRaisingEvents = true;
            previous?.Dispose();
        }
    }

    private List<RecentItemCandidate> ResolveExistingItems(CancellationToken cancellationToken)
    {
        var items = new List<RecentItemCandidate>();
        if (!Directory.Exists(_recentFolder))
        {
            return items;
        }

        foreach (var path in Directory.EnumerateFiles(_recentFolder, "*.*", SearchOption.TopDirectoryOnly)
                     .Where(IsSupportedShortcut))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var item = _resolver.Resolve(path);
            if (item is not null)
            {
                items.Add(item);
            }
        }
        return items;
    }

    private void OnShortcutChanged(object sender, FileSystemEventArgs e)
    {
        if (!IsSupportedShortcut(e.FullPath) || Volatile.Read(ref _stopping) != 0)
        {
            return;
        }

        Track(ProcessShortcutChangedAsync(e.FullPath));
    }

    private async Task ProcessShortcutChangedAsync(string path)
    {
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCancellation.Token);
        _pendingCaptures.AddOrUpdate(path, cancellation, (_, previous) =>
        {
            CancelSilently(previous);
            return cancellation;
        });

        try
        {
            await Task.Delay(250, cancellation.Token);
            if (await CaptureAsync(path, cancellation.Token))
            {
                HistoryChanged?.Invoke(this, EventArgs.Empty);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            // 같은 파일의 최신 이벤트가 이전 작업을 대체했거나 앱이 종료 중이다.
        }
        catch (Exception ex)
        {
            MonitorError?.Invoke(this, ex.Message);
        }
        finally
        {
            _pendingCaptures.TryRemove(new KeyValuePair<string, CancellationTokenSource>(path, cancellation));
            cancellation.Dispose();
        }
    }

    private async Task<bool> CaptureAsync(string path, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var item = _resolver.Resolve(path);
        if (item is null)
        {
            return false;
        }

        await _database.UpsertAsync(item, cancellationToken);
        return true;
    }

    private void OnWatcherError(object sender, ErrorEventArgs e)
    {
        if (Volatile.Read(ref _stopping) != 0 ||
            Interlocked.CompareExchange(ref _recoveryScheduled, 1, 0) != 0)
        {
            return;
        }

        Track(RecoverWatcherAsync(e.GetException()));
    }

    private async Task RecoverWatcherAsync(Exception exception)
    {
        try
        {
            MonitorError?.Invoke(this, $"{exception.Message} 전체 스캔으로 감시를 복구합니다.");
            DisposeWatcher();
            _lifetimeCancellation.Token.ThrowIfCancellationRequested();
            StartWatcherIfAvailable();
            await ScanAsync(_lifetimeCancellation.Token);
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
            // 정상 종료 중이다.
        }
        catch (Exception ex)
        {
            MonitorError?.Invoke(this, $"감시 복구 실패: {ex.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref _recoveryScheduled, 0);
        }
    }

    private void Track(Task task)
    {
        var id = Interlocked.Increment(ref _nextTaskId);
        _inFlightTasks[id] = task;
        _ = task.ContinueWith(
            completedTask => _inFlightTasks.TryRemove(id, out _),
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private static bool IsSupportedShortcut(string path)
    {
        var extension = Path.GetExtension(path);
        return extension.Equals(".lnk", StringComparison.OrdinalIgnoreCase) ||
               extension.Equals(".url", StringComparison.OrdinalIgnoreCase);
    }

    public Task StopAsync()
    {
        lock (_stopLock)
        {
            return _stopTask ??= StopCoreAsync();
        }
    }

    private async Task StopCoreAsync()
    {
        if (Interlocked.Exchange(ref _stopping, 1) != 0)
        {
            return;
        }

        _lifetimeCancellation.Cancel();
        DisposeWatcher();
        foreach (var pending in _pendingCaptures.Values)
        {
            CancelSilently(pending);
        }

        while (!_inFlightTasks.IsEmpty)
        {
            var tasks = _inFlightTasks.Values.ToArray();
            if (tasks.Length == 0)
            {
                break;
            }

            try
            {
                await Task.WhenAll(tasks);
            }
            catch (OperationCanceledException)
            {
                // 종료 취소는 예상된 흐름이다.
            }
        }

        // 수동 새로고침이나 시작 스캔이 진행 중이었다면 DB를 닫기 전에 끝날 때까지 기다린다.
        await _scanGate.WaitAsync();
        _scanGate.Release();
    }

    private void DisposeWatcher()
    {
        lock (_watcherLock)
        {
            if (_watcher is null)
            {
                return;
            }

            _watcher.EnableRaisingEvents = false;
            _watcher.Dispose();
            _watcher = null;
        }
    }

    private void ThrowIfStopped()
    {
        ObjectDisposedException.ThrowIf(_resourcesDisposed, this);
        if (Volatile.Read(ref _stopping) != 0)
        {
            throw new OperationCanceledException("최근 항목 감시기가 종료되었습니다.");
        }
    }

    private static void CancelSilently(CancellationTokenSource cancellation)
    {
        try
        {
            cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // 이벤트 교체와 완료가 동시에 일어난 경우 이미 정리된 작업이다.
        }
    }

    public void Dispose()
    {
        if (_resourcesDisposed)
        {
            return;
        }

        StopAsync().GetAwaiter().GetResult();
        DisposeResources();
    }

    public async ValueTask DisposeAsync()
    {
        if (_resourcesDisposed)
        {
            return;
        }

        await StopAsync();
        DisposeResources();
    }

    private void DisposeResources()
    {
        if (_resourcesDisposed)
        {
            return;
        }

        _resourcesDisposed = true;
        foreach (var pending in _pendingCaptures.Values)
        {
            pending.Dispose();
        }
        _pendingCaptures.Clear();
        _lifetimeCancellation.Dispose();
        _scanGate.Dispose();
    }
}
