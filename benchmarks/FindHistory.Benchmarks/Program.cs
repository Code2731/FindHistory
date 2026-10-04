using System.Diagnostics;
using FindHistory.Models;
using FindHistory.Services;
using Microsoft.Data.Sqlite;

var recentOnly = args.Any(arg => arg.Equals("--recent-only", StringComparison.OrdinalIgnoreCase));
var contentionOnly = args.Any(arg => arg.Equals("--contention-only", StringComparison.OrdinalIgnoreCase));
var exportContentionOnly = args.Any(arg => arg.Equals("--export-contention-only", StringComparison.OrdinalIgnoreCase));
var countArguments = args.Where(arg => !arg.StartsWith("--", StringComparison.Ordinal)).ToArray();
var counts = recentOnly || contentionOnly || exportContentionOnly
    ? []
    : countArguments.Length == 0
    ? new[] { 10_000, 100_000, 1_000_000 }
    : countArguments.Select(ParseCount).ToArray();
var benchmarkRoot = Path.Combine(Path.GetTempPath(), $"FindHistory-Benchmark-{Guid.NewGuid():N}");
Directory.CreateDirectory(benchmarkRoot);

Console.WriteLine($"FindHistory baseline benchmark | .NET {Environment.Version} | {Environment.OSVersion}");
Console.WriteLine("Seed time is reported separately and does not use the production collector path.");
Console.WriteLine();

try
{
    if (contentionOnly)
    {
        await MeasureDatabaseContentionAsync(benchmarkRoot);
    }
    else if (exportContentionOnly)
    {
        await MeasureExportContentionAsync(benchmarkRoot);
    }
    else
    {
        await MeasureRecentFolderAsync(benchmarkRoot);
        await MeasureCollectorThroughputAsync(benchmarkRoot);

        foreach (var count in counts)
        {
            await RunSearchScaleAsync(benchmarkRoot, count);
        }
    }
}

finally
{
    Directory.Delete(benchmarkRoot, recursive: true);
}

static async Task MeasureDatabaseContentionAsync(string root)
{
    const int concurrentWriteCount = 10_000;
    const int iterations = 3;
    var database = new RecentDatabase(Path.Combine(root, "contention", "findhistory.db"));
    await database.InitializeAsync();
    var seed = CreateCandidate(root, -1) with
    {
        TargetPath = Path.Combine(root, "anchor", "anchor.txt"),
        DisplayName = "anchor.txt",
        SourceLinkPath = Path.Combine(root, "anchor.lnk")
    };
    await database.UpsertAsync(seed);
    await database.SearchAsync("anchor", null); // warm up the read path

    Console.WriteLine($"CONTENTION | serialized DB gate | writes per batch {concurrentWriteCount:N0}");
    var baseline = new double[7];
    for (var index = 0; index < baseline.Length; index++)
    {
        var timer = Stopwatch.StartNew();
        await database.SearchAsync("anchor", null);
        timer.Stop();
        baseline[index] = timer.Elapsed.TotalMilliseconds;
    }
    Array.Sort(baseline);
    Console.WriteLine($"  idle search median | {baseline[baseline.Length / 2]:N2} ms");
    var results = new double[iterations];
    var writeTimes = new double[iterations];
    for (var iteration = 0; iteration < iterations; iteration++)
    {
        var offset = iteration * concurrentWriteCount;
        var candidates = Enumerable.Range(0, concurrentWriteCount)
            .Select(index => CreateCandidate(root, offset + index))
            .ToArray();
        var writeTimer = Stopwatch.StartNew();
        var writerStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writeTask = Task.Run(async () =>
        {
            writerStarted.SetResult();
            await database.UpsertManyAsync(candidates);
        });
        await writerStarted.Task;
        await Task.Delay(10); // let the worker acquire the DB gate before queuing a search
        var searchTimer = Stopwatch.StartNew();
        var readTask = database.SearchAsync("anchor", null);
        await readTask;
        searchTimer.Stop();
        await writeTask;
        writeTimer.Stop();
        results[iteration] = searchTimer.Elapsed.TotalMilliseconds;
        writeTimes[iteration] = writeTimer.Elapsed.TotalMilliseconds;
    }

    Array.Sort(results);
    Array.Sort(writeTimes);
    Console.WriteLine($"  write batch median | {writeTimes[iterations / 2]:N1} ms");
    Console.WriteLine($"  queued search median | {results[iterations / 2]:N1} ms");
    Console.WriteLine($"  queued search p95    | {results[^1]:N1} ms");
    Console.WriteLine("  Search is intentionally queued behind the batch in the current single-gate design.");

    foreach (var cacheMode in new[] { SqliteCacheMode.Shared, SqliteCacheMode.Private })
    {
        var caseRoot = Path.Combine(root, $"readonly-{cacheMode}");
        var caseDatabase = new RecentDatabase(Path.Combine(caseRoot, "findhistory.db"));
        await caseDatabase.InitializeAsync();
        await caseDatabase.UpsertAsync(seed with { TargetPath = seed.TargetPath + $"-{cacheMode}" });
        var readConnectionString = new SqliteConnectionStringBuilder
        {
            DataSource = caseDatabase.DatabasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = cacheMode,
            Pooling = false
        }.ToString();
        await using var reader = new SqliteConnection(readConnectionString);
        await reader.OpenAsync();
        await using (var warmup = reader.CreateCommand())
        {
            warmup.CommandText = "SELECT COUNT(*) FROM recent_items WHERE display_name LIKE '%anchor%';";
            await warmup.ExecuteScalarAsync();
        }

        var candidates = Enumerable.Range(0, concurrentWriteCount)
            .Select(index => CreateCandidate(root, 100_000 + index))
            .ToArray();
        var writeTimer = Stopwatch.StartNew();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writeTask = Task.Run(async () =>
        {
            started.SetResult();
            await caseDatabase.UpsertManyAsync(candidates);
        });
        await started.Task;
        await Task.Delay(10);
        var readTimer = Stopwatch.StartNew();
        string? error = null;
        var foundAnchor = false;
        try
        {
            await using var command = reader.CreateCommand();
            command.CommandText = "SELECT display_name FROM recent_items WHERE display_name LIKE '%anchor%' LIMIT 1;";
            var value = await command.ExecuteScalarAsync();
            foundAnchor = value is not null;
        }
        catch (SqliteException exception)
        {
            error = $"SQLite {exception.SqliteErrorCode}/{exception.SqliteExtendedErrorCode}";
        }
        readTimer.Stop();
        string? writeError = null;
        try
        {
            await writeTask;
        }
        catch (SqliteException exception)
        {
            writeError = $"writer SQLite {exception.SqliteErrorCode}/{exception.SqliteExtendedErrorCode}";
        }
        writeTimer.Stop();
        Console.WriteLine($"  read-only {cacheMode,-7} | {readTimer.Elapsed.TotalMilliseconds,8:N2} ms | " +
                          $"anchor {foundAnchor} | {(error ?? writeError ?? "ok")} | " +
                          $"write {writeTimer.Elapsed.TotalMilliseconds:N1} ms");
    }
}

static async Task MeasureExportContentionAsync(string root)
{
    const int itemCount = 100_000;
    const int iterations = 3;
    var databasePath = Path.Combine(root, "export-contention", "findhistory.db");
    var database = new RecentDatabase(databasePath);
    await database.InitializeAsync();
    var seedTime = await SeedAsync(databasePath, itemCount);
    var outputDirectory = Path.Combine(root, "exports");
    Directory.CreateDirectory(outputDirectory);
    var exportPath = Path.Combine(outputDirectory, "history.json");
    var exportTimes = new double[iterations];
    var queuedSearchTimes = new double[iterations];
    Console.WriteLine($"EXPORT CONTENTION | {itemCount:N0} items | seed {seedTime.TotalSeconds:N2} s | " +
                      $"DB {FormatBytes(GetDatabaseSize(databasePath))}");

    for (var iteration = 0; iteration < iterations; iteration++)
    {
        var exportTimer = Stopwatch.StartNew();
        var exportTask = Task.Run(() => database.ExportToAsync(exportPath, "json"));
        var exportStarted = false;
        for (var attempt = 0; attempt < 30_000 && !exportTask.IsCompleted; attempt++)
        {
            if (Directory.EnumerateFiles(outputDirectory, "*.tmp").Any())
            {
                exportStarted = true;
                break;
            }
            await Task.Delay(1);
        }
        if (!exportStarted)
        {
            await exportTask;
            throw new InvalidOperationException("Export temp file was not observed during the benchmark.");
        }

        var searchTimer = Stopwatch.StartNew();
        await database.SearchAsync("report_050000", null);
        searchTimer.Stop();
        await exportTask;
        exportTimer.Stop();
        exportTimes[iteration] = exportTimer.Elapsed.TotalMilliseconds;
        queuedSearchTimes[iteration] = searchTimer.Elapsed.TotalMilliseconds;
        Console.WriteLine($"  run {iteration + 1} | export {exportTimes[iteration]:N1} ms | " +
                          $"queued search {queuedSearchTimes[iteration]:N1} ms");
    }

    Array.Sort(exportTimes);
    Array.Sort(queuedSearchTimes);
    Console.WriteLine($"  export median | {exportTimes[iterations / 2]:N1} ms");
    Console.WriteLine($"  queued search median | {queuedSearchTimes[iterations / 2]:N1} ms");
    Console.WriteLine($"  JSON size | {FormatBytes(new FileInfo(exportPath).Length)}");
}

static async Task MeasureRecentFolderAsync(string root)
{
    var recentFolder = Environment.GetFolderPath(Environment.SpecialFolder.Recent);
    var paths = Directory.EnumerateFiles(recentFolder, "*.*", SearchOption.TopDirectoryOnly)
        .Where(path => Path.GetExtension(path).Equals(".lnk", StringComparison.OrdinalIgnoreCase) ||
                       Path.GetExtension(path).Equals(".url", StringComparison.OrdinalIgnoreCase))
        .ToArray();
    var resolver = new ShortcutResolver();
    var started = Stopwatch.StartNew();
    var candidates = paths.Select(resolver.Resolve).Where(item => item is not null).Cast<RecentItemCandidate>().ToArray();
    started.Stop();
    Console.WriteLine($"RECENT resolve   | {paths.Length:N0} shortcuts -> {candidates.Length:N0} items | " +
                      $"{started.Elapsed.TotalMilliseconds:N1} ms | " +
                      $"{(paths.Length == 0 ? 0 : paths.Length / started.Elapsed.TotalSeconds):N0} links/sec");

    var database = new RecentDatabase(Path.Combine(root, "recent-scan", "findhistory.db"));
    await database.InitializeAsync();
    started.Restart();
    await database.UpsertManyAsync(candidates);
    started.Stop();
    Console.WriteLine($"RECENT DB batch  | {candidates.Length:N0} items | {started.Elapsed.TotalMilliseconds:N1} ms");
    Console.WriteLine();
}

static async Task MeasureCollectorThroughputAsync(string root)
{
    const int itemCount = 500;
    var candidates = Enumerable.Range(0, itemCount).Select(index => CreateCandidate(root, index)).ToArray();

    var singlePath = Path.Combine(root, "collector-single", "findhistory.db");
    var singleDatabase = new RecentDatabase(singlePath);
    await singleDatabase.InitializeAsync();
    var started = Stopwatch.StartNew();
    foreach (var candidate in candidates)
    {
        await singleDatabase.UpsertAsync(candidate);
    }
    started.Stop();
    Console.WriteLine($"COLLECTOR single | {itemCount:N0} items | {started.Elapsed.TotalMilliseconds:N1} ms | " +
                      $"{itemCount / started.Elapsed.TotalSeconds:N0} items/sec");

    var batchPath = Path.Combine(root, "collector-batch", "findhistory.db");
    var batchDatabase = new RecentDatabase(batchPath);
    await batchDatabase.InitializeAsync();
    started.Restart();
    await batchDatabase.UpsertManyAsync(candidates);
    started.Stop();
    Console.WriteLine($"COLLECTOR batch  | {itemCount:N0} items | {started.Elapsed.TotalMilliseconds:N1} ms | " +
                      $"{itemCount / started.Elapsed.TotalSeconds:N0} items/sec");
    Console.WriteLine();
}

static async Task RunSearchScaleAsync(string root, int count)
{
    var path = Path.Combine(root, count.ToString(), "findhistory.db");
    var database = new RecentDatabase(path);
    await database.InitializeAsync();

    var seedTime = await SeedAsync(path, count);
    var sizeBytes = GetDatabaseSize(path);
    var targetToken = $"report_{count / 2:D7}";

    Console.WriteLine($"DATASET {count:N0} | seed {seedTime.TotalSeconds:N2} s | DB {FormatBytes(sizeBytes)}");
    await PrintMeasurementAsync("latest 1000", () => database.SearchAsync(string.Empty, null));
    await PrintMeasurementAsync("unique filename", () => database.SearchAsync(targetToken, null));
    await PrintMeasurementAsync("path + extension", () => database.SearchAsync("Project042 PDF", null));
    await PrintMeasurementAsync("extension glob", () => database.SearchAsync("*.pdf", null));
    await PrintMeasurementAsync("common token", () => database.SearchAsync("report", null));
    await PrintMeasurementAsync("missing token", () => database.SearchAsync("definitely_not_present_xyz", null));
    await PrintMeasurementAsync("recent 7 days", () => database.SearchAsync(string.Empty, DateTimeOffset.Now.AddDays(-7)));
    await PrintSnapshotMeasurementAsync("UI data + stats", () => database.SearchWithStatsAsync(targetToken, null));
    var eventRange = new HistoryDateRange(
        DateTimeOffset.UtcNow.AddDays(-7), DateTimeOffset.UtcNow.AddDays(1), "최근 7일");
    await PrintSnapshotMeasurementAsync("event date 7d", () => database.SearchWithStatsAsync(string.Empty, eventRange));
    Console.WriteLine();
}

static async Task<TimeSpan> SeedAsync(string databasePath, int count)
{
    var connectionString = new SqliteConnectionStringBuilder
    {
        DataSource = databasePath,
        Mode = SqliteOpenMode.ReadWrite,
        Pooling = false
    }.ToString();
    await using var connection = new SqliteConnection(connectionString);
    await connection.OpenAsync();

    await using (var pragma = connection.CreateCommand())
    {
        pragma.CommandText = "PRAGMA synchronous=OFF; PRAGMA temp_store=MEMORY;";
        await pragma.ExecuteNonQueryAsync();
    }

    var started = Stopwatch.StartNew();
    await using var transaction = await connection.BeginTransactionAsync();
    await using var command = connection.CreateCommand();
    command.Transaction = (SqliteTransaction)transaction;
    command.CommandText = """
        INSERT INTO recent_items (
            target_path, display_name, extension, item_kind, source_link_path,
            first_seen_utc, last_seen_utc, last_link_write_utc, open_count, exists_flag)
        VALUES ($path, $name, $extension, '파일', $link, $firstSeen, $lastSeen, $lastSeen, $count, 1);
        """;
    var pathParameter = command.Parameters.Add("$path", SqliteType.Text);
    var nameParameter = command.Parameters.Add("$name", SqliteType.Text);
    var extensionParameter = command.Parameters.Add("$extension", SqliteType.Text);
    var linkParameter = command.Parameters.Add("$link", SqliteType.Text);
    var firstSeenParameter = command.Parameters.Add("$firstSeen", SqliteType.Text);
    var lastSeenParameter = command.Parameters.Add("$lastSeen", SqliteType.Text);
    var countParameter = command.Parameters.Add("$count", SqliteType.Integer);
    command.Prepare();

    var extensions = new[] { "TXT", "PDF", "CS", "PNG", "MP4", "DOCX", "XLSX", "ZIP" };
    var now = DateTimeOffset.UtcNow;
    for (var i = 0; i < count; i++)
    {
        var extension = extensions[i % extensions.Length];
        var name = $"report_{i:D7}_{i % 97:D2}.{extension.ToLowerInvariant()}";
        var itemPath = $@"C:\Bench\Project{i % 1000:D3}\Sprint{i % 52:D2}\{name}";
        var lastSeen = now.AddMinutes(-(i % 525_600));
        pathParameter.Value = itemPath;
        nameParameter.Value = name;
        extensionParameter.Value = extension;
        linkParameter.Value = $@"C:\Recent\{i:D7}.lnk";
        firstSeenParameter.Value = lastSeen.AddDays(-30).UtcDateTime.ToString("O");
        lastSeenParameter.Value = lastSeen.UtcDateTime.ToString("O");
        countParameter.Value = 1 + i % 20;
        await command.ExecuteNonQueryAsync();
    }

    await using (var eventCommand = connection.CreateCommand())
    {
        eventCommand.Transaction = (SqliteTransaction)transaction;
        eventCommand.CommandText = """
            INSERT INTO open_events (recent_item_id, opened_utc, source_link_path, is_estimated)
            SELECT id, last_seen_utc, source_link_path, 0 FROM recent_items;
            """;
        await eventCommand.ExecuteNonQueryAsync();
    }
    await transaction.CommitAsync();
    started.Stop();

    await using var checkpoint = connection.CreateCommand();
    checkpoint.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
    await checkpoint.ExecuteNonQueryAsync();
    return started.Elapsed;
}

static async Task PrintMeasurementAsync(string name, Func<Task<IReadOnlyList<RecentItem>>> action)
{
    await action();
    const int iterations = 7;
    var timings = new double[iterations];
    var resultCount = 0;
    var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
    for (var i = 0; i < iterations; i++)
    {
        var started = Stopwatch.StartNew();
        var results = await action();
        started.Stop();
        timings[i] = started.Elapsed.TotalMilliseconds;
        resultCount = results.Count;
    }
    var allocated = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
    Array.Sort(timings);
    Console.WriteLine($"  {name,-18} | median {timings[iterations / 2],8:N2} ms | " +
                      $"p95 {timings[^1],8:N2} ms | {resultCount,4:N0} rows | " +
                      $"alloc {FormatBytes(allocated / iterations)}/op");
}

static async Task PrintSnapshotMeasurementAsync(string name, Func<Task<SearchSnapshot>> action)
{
    await action();
    const int iterations = 7;
    var timings = new double[iterations];
    var resultCount = 0;
    var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
    for (var i = 0; i < iterations; i++)
    {
        var started = Stopwatch.StartNew();
        var snapshot = await action();
        started.Stop();
        timings[i] = started.Elapsed.TotalMilliseconds;
        resultCount = snapshot.Items.Count;
    }
    var allocated = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
    Array.Sort(timings);
    Console.WriteLine($"  {name,-18} | median {timings[iterations / 2],8:N2} ms | " +
                      $"p95 {timings[^1],8:N2} ms | {resultCount,4:N0} rows | " +
                      $"alloc {FormatBytes(allocated / iterations)}/op");
}

static RecentItemCandidate CreateCandidate(string root, int index)
{
    var now = DateTimeOffset.UtcNow.AddMilliseconds(index);
    return new RecentItemCandidate(
        Path.Combine(root, "items", $"collector_{index:D5}.txt"),
        $"collector_{index:D5}.txt",
        "TXT",
        "파일",
        Path.Combine(root, "links", $"collector_{index:D5}.lnk"),
        now,
        false);
}

static long GetDatabaseSize(string path) =>
    new[] { path, path + "-wal", path + "-shm" }
        .Where(File.Exists)
        .Sum(file => new FileInfo(file).Length);

static int ParseCount(string text)
{
    var normalized = text.Trim().ToLowerInvariant();
    var multiplier = normalized.EndsWith('m') ? 1_000_000 : normalized.EndsWith('k') ? 1_000 : 1;
    if (multiplier != 1)
    {
        normalized = normalized[..^1];
    }
    return checked((int)(double.Parse(normalized, System.Globalization.CultureInfo.InvariantCulture) * multiplier));
}

static string FormatBytes(long bytes) => bytes switch
{
    >= 1024L * 1024 * 1024 => $"{bytes / 1024d / 1024 / 1024:N2} GB",
    >= 1024L * 1024 => $"{bytes / 1024d / 1024:N1} MB",
    >= 1024L => $"{bytes / 1024d:N1} KB",
    _ => $"{bytes} B"
};
