using System.Text;

namespace FindHistory.Services;

public sealed class AppLogService
{
    private const string LogFilePrefix = "findhistory-";
    private readonly object _writeLock = new();
    private readonly int _retentionDays;
    private readonly long _maximumFileBytes;
    private readonly Encoding _encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    public AppLogService(
        string? logDirectory = null,
        int retentionDays = 14,
        long maximumFileBytes = 2 * 1024 * 1024)
    {
        if (retentionDays < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(retentionDays));
        }
        if (maximumFileBytes < 128)
        {
            throw new ArgumentOutOfRangeException(nameof(maximumFileBytes));
        }

        LogDirectory = Path.GetFullPath(logDirectory ?? AppPaths.LogDirectory);
        _retentionDays = retentionDays;
        _maximumFileBytes = maximumFileBytes;
        TryPrepareDirectory();
    }

    public string LogDirectory { get; }

    public void Information(string message) => Write("INFO", message, null);

    public void Warning(string message, Exception? exception = null) =>
        Write("WARN", message, exception);

    public void Error(string message, Exception exception) => Write("ERROR", message, exception);

    private void Write(string level, string message, Exception? exception)
    {
        try
        {
            lock (_writeLock)
            {
                Directory.CreateDirectory(LogDirectory);
                var path = SelectWritableLogPath(DateTimeOffset.Now);
                var line = new StringBuilder()
                    .Append(DateTimeOffset.Now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz"))
                    .Append(" [").Append(level).Append("] ")
                    .AppendLine(message);
                if (exception is not null)
                {
                    line.AppendLine(exception.ToString());
                }
                File.AppendAllText(path, line.ToString(), _encoding);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // Logging must never prevent history collection or application shutdown.
        }
    }

    private string SelectWritableLogPath(DateTimeOffset now)
    {
        var baseName = $"{LogFilePrefix}{now:yyyy-MM-dd}";
        for (var index = 0; index < 1000; index++)
        {
            var suffix = index == 0 ? string.Empty : $".{index:D3}";
            var path = Path.Combine(LogDirectory, $"{baseName}{suffix}.log");
            if (!File.Exists(path) || new FileInfo(path).Length < _maximumFileBytes)
            {
                return path;
            }
        }

        return Path.Combine(LogDirectory, $"{baseName}.{Guid.NewGuid():N}.log");
    }

    private void TryPrepareDirectory()
    {
        try
        {
            Directory.CreateDirectory(LogDirectory);
            var threshold = DateTime.UtcNow.AddDays(-_retentionDays);
            foreach (var path in Directory.EnumerateFiles(LogDirectory, $"{LogFilePrefix}*.log"))
            {
                if (File.GetLastWriteTimeUtc(path) < threshold)
                {
                    File.Delete(path);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            // The app remains usable even when its optional diagnostic log cannot be prepared.
        }
    }
}
