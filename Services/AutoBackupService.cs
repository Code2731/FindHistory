using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace FindHistory.Services;

/// <summary>Opt-in daily snapshots. Retention applies only to this database's managed backups.</summary>
public sealed class AutoBackupService : IAsyncDisposable
{
    private readonly RecentDatabase _database;
    private readonly AppSettingsService _settings;
    private readonly AppLogService _log;
    private readonly Func<DateTimeOffset> _clock;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _stop = new();
    private readonly object _lifecycleLock = new();
    private Task? _worker;
    private Task? _disposeTask;
    private int _disposed;

    public AutoBackupService(RecentDatabase database, AppSettingsService settings,
        AppLogService log, string? backupDirectory = null, Func<DateTimeOffset>? clock = null)
    {
        _database = database;
        _settings = settings;
        _log = log;
        _clock = clock ?? (() => DateTimeOffset.Now);
        BackupDirectory = Path.GetFullPath(backupDirectory ?? AppPaths.AutoBackupDirectory);
    }

    public string BackupDirectory { get; }

    public void Start()
    {
        lock (_lifecycleLock)
        {
            ObjectDisposedException.ThrowIf(_disposed != 0, this);
            _worker ??= Task.Run(RunAsync);
        }
    }

    private async Task RunAsync()
    {
        if (_stop.IsCancellationRequested) return;
        using var timer = new PeriodicTimer(TimeSpan.FromMinutes(1));
        try
        {
            do
            {
                try { await CheckAsync(_stop.Token).ConfigureAwait(false); }
                catch (OperationCanceledException) when (_stop.IsCancellationRequested) { break; }
                catch (Exception ex) { _log.Error("Automatic backup failed. Will retry on the next check.", ex); }
            } while (await timer.WaitForNextTickAsync(_stop.Token).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
    }

    public async Task CheckAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Volatile.Read(ref _disposed) != 0) return;
            var settings = _settings.GetAutoBackupSettings();
            if (!settings.Enabled) return;

            var databasePath = _database.DatabasePath;
            var identity = Convert.ToHexString(SHA256.HashData(
                Encoding.UTF8.GetBytes(Path.GetFullPath(databasePath).ToUpperInvariant())))[..16];
            var prefix = $"FindHistory-auto-{identity}-";
            // Only exact generated names qualify. Manual backups and safety copies are never pruned.
            var pattern = new Regex("^" + Regex.Escape(prefix) +
                @"(?<stamp>\d{8}T\d{9}Z)-[a-f0-9]{32}\.fhbackup$", RegexOptions.CultureInvariant);
            var now = _clock();
            Directory.CreateDirectory(BackupDirectory);
            var backups = new List<(string Path, DateTimeOffset Time)>();
            foreach (var path in Directory.EnumerateFiles(BackupDirectory, prefix + "*.fhbackup"))
            {
                var match = pattern.Match(Path.GetFileName(path));
                if (match.Success && DateTimeOffset.TryParseExact(match.Groups["stamp"].Value,
                        "yyyyMMdd'T'HHmmssfff'Z'", CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var time) &&
                    (File.GetAttributes(path) & FileAttributes.ReparsePoint) == 0)
                    backups.Add((path, time));
            }

            if (!backups.Any(backup => backup.Time.ToOffset(now.Offset).Date == now.Date))
            {
                var destination = Path.Combine(BackupDirectory,
                    $"{prefix}{now.UtcDateTime:yyyyMMdd'T'HHmmssfff'Z'}-{Guid.NewGuid():N}.fhbackup");
                await _database.BackupToAsync(destination, cancellationToken, databasePath).ConfigureAwait(false);
                backups.Add((destination, now));
                _log.Information($"Automatic backup saved: {destination}");
            }

            // Prune only after a successful backup, or when today's backup is already present.
            foreach (var backup in backups.OrderByDescending(backup => backup.Time)
                         .ThenBy(backup => backup.Path, StringComparer.Ordinal).Skip(settings.Retention))
            {
                cancellationToken.ThrowIfCancellationRequested();
                File.Delete(backup.Path);
                _log.Information($"Expired automatic backup removed: {backup.Path}");
            }
        }
        finally { _gate.Release(); }
    }

    public ValueTask DisposeAsync()
    {
        lock (_lifecycleLock)
        {
            if (_disposeTask is null)
            {
                Volatile.Write(ref _disposed, 1);
                _stop.Cancel();
                _disposeTask = StopAsync();
            }
            return new ValueTask(_disposeTask);
        }
    }

    private async Task StopAsync()
    {
        if (_worker is not null) await _worker.ConfigureAwait(false);
        await _gate.WaitAsync().ConfigureAwait(false);
        _gate.Release();
        _stop.Dispose();
        // Keep the semaphore alive so already queued checks can safely leave it.
    }
}
