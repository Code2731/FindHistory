using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using FindHistory.Models;
using Microsoft.Data.Sqlite;

namespace FindHistory.Services;

public sealed class RecentDatabase : IDisposable, IAsyncDisposable
{
    private const int CurrentSchemaVersion = 2;
    private const int MaximumActivitySqlBuckets = 366;
    private string _connectionString;
    private string _databasePath;
    private bool _ftsAvailable;
    private readonly OperationGate _databaseGate;
    private readonly OperationGate _databasePathGate;
    private int _disposeState;
    private readonly object _disposeLock = new();
    private Task? _disposeTask;

    private const string UpsertSql = """
        INSERT INTO recent_items (
            target_path, display_name, extension, item_kind, source_link_path,
            first_seen_utc, last_seen_utc, last_link_write_utc, open_count, exists_flag)
        VALUES (
            $targetPath, $displayName, $extension, $itemKind, $sourceLinkPath,
            $seenUtc, $seenUtc, $linkWriteUtc, 1, $exists)
        ON CONFLICT(target_path) DO UPDATE SET
            display_name = excluded.display_name,
            extension = excluded.extension,
            item_kind = excluded.item_kind,
            source_link_path = excluded.source_link_path,
            last_seen_utc = CASE
                WHEN excluded.last_seen_utc > recent_items.last_seen_utc
                THEN excluded.last_seen_utc ELSE recent_items.last_seen_utc END,
            open_count = recent_items.open_count + CASE
                WHEN excluded.last_link_write_utc > recent_items.last_link_write_utc
                THEN 1 ELSE 0 END,
            last_link_write_utc = CASE
                WHEN excluded.last_link_write_utc > recent_items.last_link_write_utc
                THEN excluded.last_link_write_utc ELSE recent_items.last_link_write_utc END,
            exists_flag = excluded.exists_flag
        RETURNING id;
        """;

    public RecentDatabase(string databasePath)
    {
        _databasePath = Path.GetFullPath(databasePath);
        _connectionString = BuildConnectionString(_databasePath);
        _databaseGate = new OperationGate(() => Volatile.Read(ref _disposeState) != 0);
        _databasePathGate = new OperationGate(() => Volatile.Read(ref _disposeState) != 0);
    }

    public string DatabasePath => _databasePath;

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();

    public ValueTask DisposeAsync()
    {
        lock (_disposeLock)
        {
            if (_disposeTask is null)
            {
                Volatile.Write(ref _disposeState, 1);
                _disposeTask = DisposeCoreAsync();
            }

            return new ValueTask(_disposeTask);
        }
    }

    private async Task DisposeCoreAsync()
    {
        // 진행 중인 DB 작업과 경로 잠금 작업이 끝날 때까지 비동기로 기다린다.
        await _databaseGate.WaitForDisposalAsync().ConfigureAwait(false);
        try
        {
            await _databasePathGate.WaitForDisposalAsync().ConfigureAwait(false);
            _databasePathGate.Release();
        }
        finally
        {
            _databaseGate.Release();
        }
    }

    private sealed class OperationGate(Func<bool> isDisposing)
    {
        private readonly SemaphoreSlim _semaphore = new(1, 1);

        public async Task WaitAsync(CancellationToken cancellationToken = default)
        {
            await _semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            ThrowIfDisposing();
        }

        public void Wait()
        {
            _semaphore.Wait();
            ThrowIfDisposing();
        }

        public Task WaitForDisposalAsync() => _semaphore.WaitAsync();

        public void Release() => _semaphore.Release();

        private void ThrowIfDisposing()
        {
            if (!isDisposing())
            {
                return;
            }

            _semaphore.Release();
            throw new ObjectDisposedException(nameof(RecentDatabase));
        }
    }

    // Connection pooling stays disabled so MoveToAsync can checkpoint, close every handle,
    // and move the SQLite file without pooled native connections retaining it.
    private static string BuildConnectionString(string databasePath) =>
        new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = false
        }.ToString();

    public async Task InitializeAsync()
    {
        ThrowIfDisposed();
        await _databaseGate.WaitAsync();
        try
        {
            await InitializeCoreAsync();
        }
        finally
        {
            _databaseGate.Release();
        }
    }

    private async Task InitializeCoreAsync(CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_databasePath)!);
        await using var connection = await OpenAsync(cancellationToken);
        await using (var schemaVersionCommand = connection.CreateCommand())
        {
            schemaVersionCommand.CommandText = "PRAGMA user_version;";
            var existingVersion = Convert.ToInt32(
                await schemaVersionCommand.ExecuteScalarAsync(cancellationToken));
            if (existingVersion > CurrentSchemaVersion)
            {
                throw new InvalidDataException(
                    $"데이터베이스 스키마 버전 {existingVersion}은 이 앱에서 지원하지 않습니다 (최대 {CurrentSchemaVersion}).");
            }
        }
        await using var command = connection.CreateCommand();
        command.CommandText = """
            PRAGMA journal_mode = WAL;
            PRAGMA synchronous = NORMAL;

            CREATE TABLE IF NOT EXISTS recent_items (
                id                  INTEGER PRIMARY KEY AUTOINCREMENT,
                target_path         TEXT NOT NULL COLLATE NOCASE UNIQUE,
                display_name        TEXT NOT NULL,
                extension           TEXT NOT NULL,
                item_kind           TEXT NOT NULL,
                source_link_path    TEXT NOT NULL,
                first_seen_utc      TEXT NOT NULL,
                last_seen_utc       TEXT NOT NULL,
                last_link_write_utc TEXT NOT NULL,
                open_count          INTEGER NOT NULL DEFAULT 1,
                exists_flag         INTEGER NOT NULL DEFAULT 1
            );

            CREATE INDEX IF NOT EXISTS ix_recent_items_last_seen
                ON recent_items(last_seen_utc DESC);
            CREATE INDEX IF NOT EXISTS ix_recent_items_display_name
                ON recent_items(display_name COLLATE NOCASE);
            CREATE INDEX IF NOT EXISTS ix_recent_items_extension_last_seen
                ON recent_items(extension COLLATE NOCASE, last_seen_utc DESC);

            CREATE TABLE IF NOT EXISTS open_events (
                id                 INTEGER PRIMARY KEY AUTOINCREMENT,
                recent_item_id     INTEGER NOT NULL,
                opened_utc         TEXT NOT NULL,
                source_link_path   TEXT NOT NULL,
                is_estimated       INTEGER NOT NULL DEFAULT 0,
                UNIQUE(recent_item_id, opened_utc),
                FOREIGN KEY(recent_item_id) REFERENCES recent_items(id) ON DELETE CASCADE
            );

            CREATE INDEX IF NOT EXISTS ix_open_events_opened_item
                ON open_events(opened_utc, recent_item_id);

            CREATE TABLE IF NOT EXISTS history_stats (
                id               INTEGER PRIMARY KEY CHECK (id = 1),
                unique_items     INTEGER NOT NULL,
                total_open_count INTEGER NOT NULL,
                stored_events    INTEGER NOT NULL DEFAULT 0,
                estimated_events INTEGER NOT NULL DEFAULT 0
            );

            CREATE TRIGGER IF NOT EXISTS recent_items_stats_ai AFTER INSERT ON recent_items BEGIN
                UPDATE history_stats
                SET unique_items = unique_items + 1,
                    total_open_count = total_open_count + new.open_count
                WHERE id = 1;
            END;

            CREATE TRIGGER IF NOT EXISTS recent_items_stats_ad AFTER DELETE ON recent_items BEGIN
                UPDATE history_stats
                SET unique_items = unique_items - 1,
                    total_open_count = total_open_count - old.open_count
                WHERE id = 1;
            END;

            CREATE TRIGGER IF NOT EXISTS recent_items_stats_au
            AFTER UPDATE OF open_count ON recent_items
            WHEN new.open_count <> old.open_count BEGIN
                UPDATE history_stats
                SET total_open_count = total_open_count + new.open_count - old.open_count
                WHERE id = 1;
            END;
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
        await EnsureEventStatsSchemaAsync(connection, cancellationToken);
        await MigrateSchemaAsync(connection, cancellationToken);
        _ftsAvailable = await EnsureSearchIndexAsync(connection, cancellationToken);
    }

    private static async Task MigrateSchemaAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var versionCommand = connection.CreateCommand();
        versionCommand.CommandText = "PRAGMA user_version;";
        var version = Convert.ToInt32(await versionCommand.ExecuteScalarAsync(cancellationToken));
        if (version > CurrentSchemaVersion)
        {
            throw new InvalidDataException(
                $"데이터베이스 스키마 버전 {version}은 이 앱에서 지원하지 않습니다 (최대 {CurrentSchemaVersion}).");
        }
        if (version == CurrentSchemaVersion)
        {
            return;
        }

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var migration = connection.CreateCommand();
        migration.Transaction = (SqliteTransaction)transaction;
        migration.CommandText = """
            INSERT OR IGNORE INTO open_events (
                recent_item_id, opened_utc, source_link_path, is_estimated)
            SELECT id, last_seen_utc, source_link_path, 1
            FROM recent_items;

            INSERT OR IGNORE INTO history_stats (
                id, unique_items, total_open_count, stored_events, estimated_events)
            SELECT 1,
                   COUNT(*),
                   COALESCE(SUM(open_count), 0),
                   (SELECT COUNT(*) FROM open_events),
                   (SELECT COUNT(*) FROM open_events WHERE is_estimated = 1)
            FROM recent_items;

            UPDATE history_stats
            SET stored_events = (SELECT COUNT(*) FROM open_events),
                estimated_events = (SELECT COUNT(*) FROM open_events WHERE is_estimated = 1)
            WHERE id = 1;

            PRAGMA user_version = 2;
            """;
        await migration.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private static async Task EnsureEventStatsSchemaAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using (var columnsCommand = connection.CreateCommand())
        {
            columnsCommand.CommandText = "PRAGMA table_info(history_stats);";
            await using var reader = await columnsCommand.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                columns.Add(reader.GetString(1));
            }
        }

        await using var command = connection.CreateCommand();
        var statements = new List<string>();
        if (!columns.Contains("stored_events"))
        {
            statements.Add("ALTER TABLE history_stats ADD COLUMN stored_events INTEGER NOT NULL DEFAULT 0;");
        }
        if (!columns.Contains("estimated_events"))
        {
            statements.Add("ALTER TABLE history_stats ADD COLUMN estimated_events INTEGER NOT NULL DEFAULT 0;");
        }

        statements.AddRange(
        [
            """
            CREATE TRIGGER IF NOT EXISTS open_events_stats_ai AFTER INSERT ON open_events BEGIN
                UPDATE history_stats
                SET stored_events = stored_events + 1,
                    estimated_events = estimated_events + new.is_estimated
                WHERE id = 1;
            END;
            """,
            """
            CREATE TRIGGER IF NOT EXISTS open_events_stats_ad AFTER DELETE ON open_events BEGIN
                UPDATE history_stats
                SET stored_events = stored_events - 1,
                    estimated_events = estimated_events - old.is_estimated
                WHERE id = 1;
            END;
            """,
            """
            CREATE TRIGGER IF NOT EXISTS open_events_stats_au
            AFTER UPDATE OF is_estimated ON open_events
            WHEN new.is_estimated <> old.is_estimated BEGIN
                UPDATE history_stats
                SET estimated_events = estimated_events + new.is_estimated - old.is_estimated
                WHERE id = 1;
            END;
            """
        ]);
        command.CommandText = string.Join(Environment.NewLine, statements);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task UpsertAsync(RecentItemCandidate item, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _databaseGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            await using var command = CreateUpsertCommand(connection, (SqliteTransaction)transaction);
            await using var eventCommand = CreateOpenEventCommand(connection, (SqliteTransaction)transaction);
            BindUpsertParameters(command, item);
            var itemId = Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));
            await InsertOpenEventAsync(eventCommand, itemId, item, cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        finally
        {
            _databaseGate.Release();
        }
    }

    public async Task UpsertManyAsync(IReadOnlyCollection<RecentItemCandidate> items,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        if (items.Count == 0)
        {
            return;
        }

        await _databaseGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
            await using var command = CreateUpsertCommand(connection, (SqliteTransaction)transaction);
            await using var eventCommand = CreateOpenEventCommand(connection, (SqliteTransaction)transaction);
            foreach (var item in items)
            {
                BindUpsertParameters(command, item);
                var itemId = Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken));
                await InsertOpenEventAsync(eventCommand, itemId, item, cancellationToken);
            }
            await transaction.CommitAsync(cancellationToken);
        }
        finally
        {
            _databaseGate.Release();
        }
    }

    public async Task<IReadOnlyList<RecentItem>> SearchAsync(
        string searchText,
        DateTimeOffset? since,
        int limit = 1000,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _databaseGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            return await SearchCoreAsync(connection, searchText, since, null, limit, cancellationToken);
        }
        finally
        {
            _databaseGate.Release();
        }
    }

    public async Task<HistoryStats> GetStatsAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _databaseGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            return await GetStatsCoreAsync(connection, cancellationToken);
        }
        finally
        {
            _databaseGate.Release();
        }
    }

    public Task ExportToAsync(
        string destinationPath, string format, CancellationToken cancellationToken = default) =>
        ExportCoreAsync(destinationPath, format, null, cancellationToken);

    public Task ExportItemsToAsync(
        string destinationPath,
        string format,
        IReadOnlyCollection<long> itemIds,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(itemIds);
        var distinctIds = itemIds.Distinct().ToArray();
        if (distinctIds.Length == 0)
        {
            throw new ArgumentException("내보낼 항목이 없습니다.", nameof(itemIds));
        }

        return ExportCoreAsync(destinationPath, format, distinctIds, cancellationToken);
    }

    public Task ExportSearchToAsync(string destinationPath, string format, string searchText,
        HistoryDateRange? dateRange, HistoryFilters? filters = null,
        CancellationToken cancellationToken = default)
    {
        if (dateRange is not null && dateRange.EndUtc <= dateRange.StartUtc)
            throw new ArgumentException("내보내기 날짜 범위가 유효하지 않습니다.", nameof(dateRange));
        var selection = new HistoryExportSelection(searchText, dateRange, filters);
        return Task.Run(() => ExportCoreAsync(destinationPath, format, null, cancellationToken, selection),
            cancellationToken);
    }

    private async Task ExportCoreAsync(
        string destinationPath,
        string format,
        IReadOnlyCollection<long>? itemIds,
        CancellationToken cancellationToken,
        HistoryExportSelection? selection = null)
    {
        ThrowIfDisposed();
        var targetPath = Path.GetFullPath(destinationPath);
        var normalizedFormat = format.Trim().TrimStart('.').ToLowerInvariant();
        if (normalizedFormat is not ("csv" or "json"))
        {
            throw new ArgumentOutOfRangeException(nameof(format), "내보내기 형식은 CSV 또는 JSON이어야 합니다.");
        }

        await _databasePathGate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            if (string.Equals(targetPath, _databasePath, StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException("내보내기 파일은 현재 데이터베이스와 다른 경로여야 합니다.");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            var temporaryPath = targetPath + $".{Guid.NewGuid():N}.tmp";
            try
            {
                var readOnlyConnectionString = new SqliteConnectionStringBuilder
                {
                    DataSource = _databasePath,
                    Mode = SqliteOpenMode.ReadOnly,
                    Cache = SqliteCacheMode.Private,
                    Pooling = false
                }.ToString();
                await using var connection = new SqliteConnection(readOnlyConnectionString);
                await connection.OpenAsync(cancellationToken);
                await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
                await using var command = connection.CreateCommand();
                command.Transaction = (SqliteTransaction)transaction;
                var conditions = new List<string>();
                if (itemIds is not null)
                {
                    var parameterNames = new List<string>(itemIds.Count);
                    var parameterIndex = 0;
                    foreach (var itemId in itemIds)
                    {
                        var parameterName = $"$itemId{parameterIndex++}";
                        parameterNames.Add(parameterName);
                        command.Parameters.AddWithValue(parameterName, itemId);
                    }

                    conditions.Add($"r.id IN ({string.Join(", ", parameterNames)})");
                }

                if (selection is not null)
                {
                    conditions.AddRange(BuildItemFilterConditions(command, selection.Filters));
                    AddTextSearchConditions(command, conditions, selection.SearchText.Split(' ',
                        StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
                }
                var eventJoin = "LEFT JOIN open_events AS e ON e.recent_item_id = r.id";
                if (selection?.DateRange is { } range)
                {
                    eventJoin = """
                        INNER JOIN open_events AS e ON e.recent_item_id = r.id
                        AND e.opened_utc >= $exportStartUtc AND e.opened_utc < $exportEndUtc
                        """;
                    command.Parameters.AddWithValue("$exportStartUtc", range.StartUtc.UtcDateTime.ToString("O"));
                    command.Parameters.AddWithValue("$exportEndUtc", range.EndUtc.UtcDateTime.ToString("O"));
                }
                var itemFilter = conditions.Count == 0 ? string.Empty
                    : $"WHERE {string.Join(" AND ", conditions)}";

                command.CommandText = $"""
                    SELECT r.id, r.target_path, r.display_name, r.extension, r.item_kind,
                           r.source_link_path, r.first_seen_utc, r.last_seen_utc,
                           r.last_link_write_utc, r.open_count, r.exists_flag,
                           e.opened_utc, e.source_link_path, e.is_estimated
                    FROM recent_items AS r
                    {eventJoin}
                    {itemFilter}
                    ORDER BY r.id, e.opened_utc, e.id;
                    """;
                await using var reader = await command.ExecuteReaderAsync(cancellationToken);
                await using (var output = new FileStream(temporaryPath, FileMode.CreateNew,
                                 FileAccess.Write, FileShare.None, 64 * 1024, useAsync: true))
                {
                    if (normalizedFormat == "csv")
                    {
                        await WriteCsvExportAsync(reader, output, cancellationToken);
                    }
                    else
                    {
                        await WriteJsonExportAsync(reader, output, cancellationToken, selection);
                    }
                }

                await transaction.CommitAsync(cancellationToken);
                File.Move(temporaryPath, targetPath, overwrite: true);
            }
            finally
            {
                DeleteCheckpointSidecar(temporaryPath);
            }
        }
        finally
        {
            _databasePathGate.Release();
        }
    }

    private static async Task WriteCsvExportAsync(
        SqliteDataReader reader, Stream output, CancellationToken cancellationToken)
    {
        await using var writer = new StreamWriter(output, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true),
            64 * 1024, leaveOpen: true);
        await writer.WriteLineAsync(
            "ItemId,TargetPath,DisplayName,Extension,ItemKind,SourceLinkPath,FirstSeenUtc,LastSeenUtc," +
            "LastLinkWriteUtc,OpenCount,Exists,EventOpenedUtc,EventSourceLinkPath,EventIsEstimated");
        while (await reader.ReadAsync(cancellationToken))
        {
            var itemValues = new string[]
            {
                reader.GetInt64(0).ToString(CultureInfo.InvariantCulture),
                CsvCell(reader.GetString(1)), CsvCell(reader.GetString(2)), CsvCell(reader.GetString(3)),
                CsvCell(reader.GetString(4)), CsvCell(reader.GetString(5)), CsvCell(reader.GetString(6)),
                CsvCell(reader.GetString(7)), CsvCell(reader.GetString(8)),
                reader.GetInt32(9).ToString(CultureInfo.InvariantCulture),
                reader.GetInt32(10) == 1 ? "true" : "false"
            };
            var eventValues = reader.IsDBNull(11)
                ? new[] { "", "", "" }
                : new[]
                {
                    CsvCell(reader.GetString(11)), CsvCell(reader.GetString(12)),
                    reader.GetInt32(13) == 1 ? "true" : "false"
                };
            await writer.WriteLineAsync(string.Join(",", itemValues.Concat(eventValues)));
        }

        await writer.FlushAsync(cancellationToken);
    }

    private static async Task WriteJsonExportAsync(
        SqliteDataReader reader, Stream output, CancellationToken cancellationToken,
        HistoryExportSelection? selection = null)
    {
        using var writer = new Utf8JsonWriter(output, new JsonWriterOptions
        {
            Indented = true,
            // This encoder is for a standalone JSON data file. HTML embedding needs context-aware encoding.
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        });
        var options = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        writer.WriteStartObject();
        writer.WriteNumber("schemaVersion", 1);
        writer.WriteString("exportedAtUtc", DateTimeOffset.UtcNow);
        if (selection is not null)
        {
            writer.WritePropertyName("selection");
            JsonSerializer.Serialize(writer, selection, options);
            writer.WriteString("itemMetadataScope", "lifetime");
            writer.WriteString("eventScope", selection.DateRange is null ? "lifetime" : "selectedDateRange");
        }
        writer.WriteStartArray("items");

        HistoryExportItem? currentItem = null;
        List<HistoryExportEvent>? currentEvents = null;
        while (await reader.ReadAsync(cancellationToken))
        {
            var itemId = reader.GetInt64(0);
            if (currentItem?.Id != itemId)
            {
                if (currentItem is not null)
                {
                    JsonSerializer.Serialize(writer, currentItem with { Events = currentEvents! }, options);
                }

                currentItem = new HistoryExportItem(
                    itemId, reader.GetString(1), reader.GetString(2), reader.GetString(3),
                    reader.GetString(4), reader.GetString(5), ParseExportDate(reader.GetString(6)),
                    ParseExportDate(reader.GetString(7)), ParseExportDate(reader.GetString(8)),
                    reader.GetInt32(9), reader.GetInt32(10) == 1, []);
                currentEvents = [];
            }

            if (!reader.IsDBNull(11))
            {
                currentEvents!.Add(new HistoryExportEvent(
                    ParseExportDate(reader.GetString(11)), reader.GetString(12), reader.GetInt32(13) == 1));
            }
        }

        if (currentItem is not null)
        {
            JsonSerializer.Serialize(writer, currentItem with { Events = currentEvents! }, options);
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
        await writer.FlushAsync(cancellationToken);
    }

    private static DateTimeOffset ParseExportDate(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);

    private static string CsvCell(string value)
    {
        var firstSignificant = value.TrimStart(' ', '\t', '\r', '\n');
        var safeValue = firstSignificant.Length > 0 && firstSignificant[0] is '=' or '+' or '-' or '@'
            ? "'" + value
            : value;
        return $"\"{safeValue.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
    }

    public Task<DatabaseStorageStatistics> GetStorageStatisticsAsync(
        CancellationToken cancellationToken = default) => Task.Run(async () =>
    {
        ThrowIfDisposed();
        await _databaseGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
            return await ReadStorageStatisticsAsync(connection, cancellationToken).ConfigureAwait(false);
        }
        finally { _databaseGate.Release(); }
    }, cancellationToken);

    public Task<DatabaseStorageStatistics> CompactAsync(string expectedDatabasePath,
        CancellationToken cancellationToken = default) => Task.Run(async () =>
    {
        ThrowIfDisposed();
        await _databaseGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Export holds the path gate. Wait for its snapshot before rewriting the database.
            await _databasePathGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (!string.Equals(Path.GetFullPath(expectedDatabasePath), _databasePath,
                        StringComparison.OrdinalIgnoreCase))
                    throw new IOException("공간 정리 준비 중 데이터베이스가 변경되었습니다.");
                await using var connection = await OpenAsync(cancellationToken).ConfigureAwait(false);
                await CheckpointForCompactionAsync(connection, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                await using var command = connection.CreateCommand();
                // Microsoft.Data.Sqlite executes synchronously. Task.Run keeps VACUUM off the UI thread.
                // VACUUM is atomic and preserves history. Once started, allow it to finish safely.
                command.CommandText = "VACUUM;";
                command.ExecuteNonQuery();
                await CheckpointForCompactionAsync(connection, CancellationToken.None).ConfigureAwait(false);
                return await ReadStorageStatisticsAsync(connection, CancellationToken.None).ConfigureAwait(false);
            }
            finally { _databasePathGate.Release(); }
        }
        finally { _databaseGate.Release(); }
    }, cancellationToken);

    private static async Task CheckpointForCompactionAsync(SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false) || reader.GetInt64(0) != 0)
            throw new IOException("다른 프로그램이 DB를 사용 중입니다. 잠시 후 다시 시도하세요.");
    }

    private async Task<DatabaseStorageStatistics> ReadStorageStatisticsAsync(SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        async Task<long> ReadPragmaAsync(string sql)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false));
        }
        var pageSize = await ReadPragmaAsync("PRAGMA page_size;").ConfigureAwait(false);
        var pageCount = await ReadPragmaAsync("PRAGMA page_count;").ConfigureAwait(false);
        var freeCount = await ReadPragmaAsync("PRAGMA freelist_count;").ConfigureAwait(false);
        static long FileBytes(string path)
        {
            try { return new FileInfo(path).Length; }
            catch (FileNotFoundException) { return 0; }
        }
        return new DatabaseStorageStatistics(_databasePath, FileBytes(_databasePath),
            FileBytes(_databasePath + "-wal"), FileBytes(_databasePath + "-shm"),
            checked(pageCount * pageSize), checked(freeCount * pageSize));
    }

    public async Task<DatabaseDiagnostics> GetDiagnosticsAsync(
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _databaseGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT
                    hs.unique_items,
                    hs.total_open_count,
                    hs.stored_events,
                    hs.estimated_events
                FROM history_stats hs
                WHERE hs.id = 1;
                """;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                return new DatabaseDiagnostics(0, 0, 0, 0);
            }

            return new DatabaseDiagnostics(
                reader.GetInt64(0),
                reader.GetInt64(1),
                reader.GetInt64(2),
                reader.GetInt64(3));
        }
        finally
        {
            _databaseGate.Release();
        }
    }

    public async Task<SearchSnapshot> SearchWithStatsAsync(
        string searchText,
        HistoryDateRange? dateRange,
        int limit = 1000,
        CancellationToken cancellationToken = default,
        HistoryFilters? filters = null)
    {
        ThrowIfDisposed();
        await _databaseGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            var items = await SearchCoreAsync(connection, searchText, null, dateRange, limit, cancellationToken, filters);
            var stats = await GetStatsCoreAsync(connection, cancellationToken);
            return new SearchSnapshot(items, stats);
        }
        finally
        {
            _databaseGate.Release();
        }
    }

    public async Task<IReadOnlyList<DailyActivity>> GetDailyActivityAsync(
        HistoryDateRange dateRange,
        TimeZoneInfo? timeZone = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        await _databaseGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            var zone = timeZone ?? TimeZoneInfo.Local;
            var firstLocalDate = TimeZoneInfo.ConvertTime(dateRange.StartUtc, zone).Date;
            var lastLocalDate = TimeZoneInfo.ConvertTime(dateRange.EndUtc.AddTicks(-1), zone).Date;
            var dayCount = (lastLocalDate - firstLocalDate).Days + 1;
            if (dayCount > MaximumActivitySqlBuckets)
            {
                return await ReadActivityEventsAsync(connection, dateRange, zone, cancellationToken);
            }

            var buckets = new List<(DateTime Date, DateTimeOffset StartUtc, DateTimeOffset EndUtc)>();
            for (var date = firstLocalDate; date <= lastLocalDate; date = date.AddDays(1))
            {
                var dayRange = HistoryDateRangeFactory.CreateLocalCalendarRange(
                    date, date.AddDays(1), date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), zone);
                var startUtc = dayRange.StartUtc > dateRange.StartUtc ? dayRange.StartUtc : dateRange.StartUtc;
                var endUtc = dayRange.EndUtc < dateRange.EndUtc ? dayRange.EndUtc : dateRange.EndUtc;
                if (startUtc < endUtc)
                {
                    buckets.Add((date, startUtc, endUtc));
                }
            }

            if (buckets.Count == 0)
            {
                return [];
            }

            await using var command = connection.CreateCommand();
            var values = new List<string>(buckets.Count);
            for (var index = 0; index < buckets.Count; index++)
            {
                var bucket = buckets[index];
                var dateName = $"$date{index}";
                var startName = $"$start{index}";
                var endName = $"$end{index}";
                values.Add($"({dateName}, {startName}, {endName})");
                command.Parameters.AddWithValue(dateName,
                    bucket.Date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture));
                command.Parameters.AddWithValue(startName,
                    bucket.StartUtc.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
                command.Parameters.AddWithValue(endName,
                    bucket.EndUtc.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
            }

            command.CommandText = $"""
                WITH day_ranges(local_date, start_utc, end_utc) AS (
                    VALUES {string.Join(", ", values)}
                )
                SELECT day_ranges.local_date, COUNT(open_events.id), MAX(open_events.is_estimated)
                FROM day_ranges
                JOIN open_events
                  ON open_events.opened_utc >= day_ranges.start_utc
                 AND open_events.opened_utc < day_ranges.end_utc
                GROUP BY day_ranges.local_date
                ORDER BY day_ranges.local_date;
                """;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            var results = new List<DailyActivity>();
            while (await reader.ReadAsync(cancellationToken))
            {
                var localDate = DateTime.ParseExact(reader.GetString(0), "yyyy-MM-dd",
                    CultureInfo.InvariantCulture, DateTimeStyles.None);
                results.Add(new DailyActivity(localDate, reader.GetInt32(1), reader.GetInt32(2) == 1));
            }

            return results;
        }
        finally
        {
            _databaseGate.Release();
        }
    }

    private static async Task<IReadOnlyList<DailyActivity>> ReadActivityEventsAsync(
        SqliteConnection connection,
        HistoryDateRange dateRange,
        TimeZoneInfo zone,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT opened_utc, is_estimated
            FROM open_events
            WHERE opened_utc >= $rangeStartUtc AND opened_utc < $rangeEndUtc
            ORDER BY opened_utc;
            """;
        command.Parameters.AddWithValue(
            "$rangeStartUtc", dateRange.StartUtc.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue(
            "$rangeEndUtc", dateRange.EndUtc.UtcDateTime.ToString("O", CultureInfo.InvariantCulture));

        var totals = new Dictionary<DateTime, (int Count, bool ContainsEstimated)>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var openedUtc = DateTimeOffset.Parse(
                reader.GetString(0), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
            var localDate = TimeZoneInfo.ConvertTime(openedUtc, zone).Date;
            totals.TryGetValue(localDate, out var total);
            totals[localDate] = (total.Count + 1, total.ContainsEstimated || reader.GetInt32(1) == 1);
        }

        return totals.OrderBy(pair => pair.Key)
            .Select(pair => new DailyActivity(pair.Key, pair.Value.Count, pair.Value.ContainsEstimated))
            .ToArray();
    }

    public async Task<bool> MoveToAsync(
        string destinationPath,
        Action<string>? persistNewPath = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var targetPath = Path.GetFullPath(destinationPath);
        await _databaseGate.WaitAsync(cancellationToken);
        try
        {
            await _databasePathGate.WaitAsync(cancellationToken);
            try
            {
                if (string.Equals(targetPath, _databasePath, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
                if (File.Exists(targetPath))
                {
                    throw new IOException("선택한 위치에 findhistory.db가 이미 있습니다.");
                }

                Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
                var sourcePath = _databasePath;
                var sourceFtsAvailable = _ftsAvailable;
                var temporaryPath = targetPath + $".{Guid.NewGuid():N}.tmp";
                var targetCreated = false;
                var pathPersisted = false;
                try
                {
                    await BackupDatabaseFileAsync(sourcePath, temporaryPath, cancellationToken);
                    await ValidateFindHistoryDatabaseAsync(temporaryPath, cancellationToken);
                    File.Move(temporaryPath, targetPath);
                    targetCreated = true;

                    // The old database remains intact until the new path is persisted and opened.
                    persistNewPath?.Invoke(targetPath);
                    pathPersisted = persistNewPath is not null;
                    _databasePath = targetPath;
                    _connectionString = BuildConnectionString(_databasePath);
                    try
                    {
                        await InitializeCoreAsync(cancellationToken);
                    }
                    catch
                    {
                        _databasePath = sourcePath;
                        _connectionString = BuildConnectionString(sourcePath);
                        _ftsAvailable = sourceFtsAvailable;
                        throw;
                    }
                }
                finally
                {
                    DeleteCheckpointSidecar(temporaryPath);
                    DeleteCheckpointSidecar(temporaryPath + "-wal");
                    DeleteCheckpointSidecar(temporaryPath + "-shm");
                    if (targetCreated && !pathPersisted &&
                        !string.Equals(_databasePath, targetPath, StringComparison.OrdinalIgnoreCase))
                    {
                        TryDeleteDatabaseFiles(targetPath);
                    }
                }

                var previousDatabaseRemoved = TryDeleteDatabaseFiles(sourcePath);
                return previousDatabaseRemoved;
            }
            finally
            {
                _databasePathGate.Release();
            }
        }
        finally
        {
            _databaseGate.Release();
        }
    }

    public async Task BackupToAsync(string destinationPath, CancellationToken cancellationToken = default,
        string? expectedDatabasePath = null)
    {
        ThrowIfDisposed();
        var targetPath = Path.GetFullPath(destinationPath);
        await _databaseGate.WaitAsync(cancellationToken);
        try
        {
            if (expectedDatabasePath is not null && !string.Equals(
                    expectedDatabasePath, _databasePath, StringComparison.OrdinalIgnoreCase))
                throw new IOException("자동 백업 준비 중 데이터베이스가 변경되었습니다.");
            if (string.Equals(targetPath, _databasePath, StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException("백업 파일은 현재 데이터베이스와 다른 경로여야 합니다.");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            var temporaryPath = targetPath + $".{Guid.NewGuid():N}.tmp";
            try
            {
                await BackupDatabaseFileAsync(_databasePath, temporaryPath, cancellationToken);
                File.Move(temporaryPath, targetPath, overwrite: true);
            }
            finally
            {
                DeleteCheckpointSidecar(temporaryPath);
                DeleteCheckpointSidecar(temporaryPath + "-wal");
                DeleteCheckpointSidecar(temporaryPath + "-shm");
            }
        }
        finally
        {
            _databaseGate.Release();
        }
    }

    public async Task<string> RestoreFromAsync(string backupPath, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var sourcePath = Path.GetFullPath(backupPath);
        if (!File.Exists(sourcePath))
        {
            throw new FileNotFoundException("백업 파일을 찾을 수 없습니다.", sourcePath);
        }

        await _databaseGate.WaitAsync(cancellationToken);
        try
        {
            await _databasePathGate.WaitAsync(cancellationToken);
            try
            {
                if (string.Equals(sourcePath, _databasePath, StringComparison.OrdinalIgnoreCase))
                {
                    throw new IOException("현재 데이터베이스 파일 자체는 복원 원본으로 선택할 수 없습니다.");
                }

                await ValidateFindHistoryDatabaseAsync(sourcePath, cancellationToken);
                var directory = Path.GetDirectoryName(_databasePath)!;
                var stagedPath = Path.Combine(directory, $"findhistory-restore-{Guid.NewGuid():N}.tmp");
                var safetyPath = _databasePath + $".before-restore-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}.bak";
                try
                {
                    await BackupDatabaseFileAsync(sourcePath, stagedPath, cancellationToken);
                    await ValidateFindHistoryDatabaseAsync(stagedPath, cancellationToken);
                    await CheckpointAsync(cancellationToken);
                    await BackupDatabaseFileAsync(_databasePath, safetyPath, cancellationToken);
                    File.Move(stagedPath, _databasePath, overwrite: true);
                    DeleteCheckpointSidecar(_databasePath + "-wal");
                    DeleteCheckpointSidecar(_databasePath + "-shm");
                    try
                    {
                        await InitializeCoreAsync(cancellationToken);
                    }
                    catch
                    {
                        DeleteCheckpointSidecar(_databasePath + "-wal");
                        DeleteCheckpointSidecar(_databasePath + "-shm");
                        File.Copy(safetyPath, _databasePath, overwrite: true);
                        await InitializeCoreAsync(cancellationToken);
                        throw;
                    }
                    return safetyPath;
                }
                finally
                {
                    DeleteCheckpointSidecar(stagedPath);
                    DeleteCheckpointSidecar(stagedPath + "-wal");
                    DeleteCheckpointSidecar(stagedPath + "-shm");
                }
            }
            finally
            {
                _databasePathGate.Release();
            }
        }
        finally
        {
            _databaseGate.Release();
        }
    }

    private static async Task BackupDatabaseFileAsync(
        string sourcePath, string destinationPath, CancellationToken cancellationToken)
    {
        var sourceConnectionString = new SqliteConnectionStringBuilder
        {
            DataSource = sourcePath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            Pooling = false
        }.ToString();
        var destinationConnectionString = new SqliteConnectionStringBuilder
        {
            DataSource = destinationPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = false
        }.ToString();
        await using var source = new SqliteConnection(sourceConnectionString);
        await source.OpenAsync(cancellationToken);
        await using var destination = new SqliteConnection(destinationConnectionString);
        await destination.OpenAsync(cancellationToken);
        await Task.Run(() => source.BackupDatabase(destination), cancellationToken);
    }

    public async Task UseAsync(string databasePath, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var targetPath = Path.GetFullPath(databasePath);
        if (!File.Exists(targetPath))
        {
            throw new FileNotFoundException("선택한 데이터베이스 파일을 찾을 수 없습니다.", targetPath);
        }

        await _databaseGate.WaitAsync(cancellationToken);
        try
        {
            await _databasePathGate.WaitAsync(cancellationToken);
            try
            {
                await ValidateFindHistoryDatabaseAsync(targetPath, cancellationToken);
                var previousPath = _databasePath;
                var previousConnectionString = _connectionString;
                var previousFtsAvailable = _ftsAvailable;
                _databasePath = targetPath;
                _connectionString = BuildConnectionString(_databasePath);
                try
                {
                    await InitializeCoreAsync(cancellationToken);
                }
                catch
                {
                    _databasePath = previousPath;
                    _connectionString = previousConnectionString;
                    _ftsAvailable = previousFtsAvailable;
                    throw;
                }
            }
            finally
            {
                _databasePathGate.Release();
            }
        }
        finally
        {
            _databaseGate.Release();
        }
    }

    private async Task CheckpointAsync(CancellationToken cancellationToken)
    {
        await using var connection = await OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void DeleteCheckpointSidecar(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // The main database is already checkpointed and safely moved.
        }
    }

    private static bool TryDeleteDatabaseFiles(string databasePath)
    {
        var succeeded = true;
        foreach (var path in new[] { databasePath, databasePath + "-wal", databasePath + "-shm" })
        {
            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
                succeeded = false;
            }
            catch (UnauthorizedAccessException)
            {
                succeeded = false;
            }
        }

        return succeeded;
    }

    private static async Task ValidateFindHistoryDatabaseAsync(string databasePath,
        CancellationToken cancellationToken)
    {
        var readOnlyConnectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString();

        await using var connection = new SqliteConnection(readOnlyConnectionString);
        await connection.OpenAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='recent_items';";
        var tableCount = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
        if (tableCount != 1)
        {
            throw new InvalidDataException("FindHistory 데이터베이스 형식이 아닙니다.");
        }

        command.CommandText = "PRAGMA user_version;";
        var schemaVersion = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
        if (schemaVersion > CurrentSchemaVersion)
        {
            throw new InvalidDataException(
                $"선택한 데이터베이스의 스키마 버전 {schemaVersion}은 지원되지 않습니다.");
        }

        command.CommandText = "PRAGMA table_info(recent_items);";
        var columns = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                columns.Add(reader.GetString(1));
            }
        }

        string[] requiredColumns =
        [
            "id", "target_path", "display_name", "extension", "item_kind",
            "source_link_path", "first_seen_utc", "last_seen_utc",
            "last_link_write_utc", "open_count", "exists_flag"
        ];
        var missingColumns = requiredColumns.Where(column => !columns.Contains(column)).ToArray();
        if (missingColumns.Length > 0)
        {
            throw new InvalidDataException(
                $"선택한 DB의 recent_items 테이블에 필수 열이 없습니다: {string.Join(", ", missingColumns)}");
        }
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken = default)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(
        Volatile.Read(ref _disposeState) != 0, this);

    private static List<string> BuildItemFilterConditions(SqliteCommand command, HistoryFilters? filters)
    {
        var conditions = new List<string>();
        if (!string.IsNullOrWhiteSpace(filters?.Extension))
        {
            conditions.Add("r.extension = $extension COLLATE NOCASE");
            command.Parameters.AddWithValue("$extension", filters.Extension.Trim().TrimStart('*', '.'));
        }
        if (filters?.Exists is { } exists)
        {
            conditions.Add("r.exists_flag = $exists");
            command.Parameters.AddWithValue("$exists", exists ? 1 : 0);
        }
        if (!string.IsNullOrWhiteSpace(filters?.Folder))
        {
            var folder = NormalizeFolderFilterPath(filters.Folder);
            var prefix = folder + Path.DirectorySeparatorChar;
            var escapedPrefix = prefix.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
            conditions.Add("r.target_path LIKE $folderPrefix ESCAPE '\\'");
            command.Parameters.AddWithValue("$folderPrefix", escapedPrefix + "%");
        }
        return conditions;
    }

    private static void AddTextSearchConditions(SqliteCommand command, List<string> conditions, string[] tokens)
    {
        for (var i = 0; i < tokens.Length; i++)
        {
            if (TryGetExactExtensionPattern(tokens[i], out var extension))
            {
                conditions.Add($"r.extension = $query{i} COLLATE NOCASE");
                command.Parameters.AddWithValue($"$query{i}", extension);
            }
            else
            {
                conditions.Add($"(r.display_name LIKE $query{i} ESCAPE '\\' OR r.target_path LIKE $query{i} ESCAPE '\\')");
                command.Parameters.AddWithValue($"$query{i}", BuildLikePattern(tokens[i]));
            }
        }
    }

    private async Task<IReadOnlyList<RecentItem>> SearchCoreAsync(
        SqliteConnection connection,
        string searchText,
        DateTimeOffset? since,
        HistoryDateRange? eventRange,
        int limit,
        CancellationToken cancellationToken,
        HistoryFilters? filters = null)
    {
        await using var command = connection.CreateCommand();

        var conditions = BuildItemFilterConditions(command, filters);
        var tokens = searchText.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var ftsQuery = BuildFtsQuery(tokens);
        var useFts = ftsQuery is not null && _ftsAvailable &&
                     await IsSelectiveFtsQueryAsync(connection, ftsQuery, limit, cancellationToken);
        if (useFts)
        {
            conditions.Add("r.id IN (SELECT rowid FROM recent_items_fts WHERE recent_items_fts MATCH $ftsQuery)");
            command.Parameters.AddWithValue("$ftsQuery", ftsQuery!);
        }
        else
        {
            AddTextSearchConditions(command, conditions, tokens);
        }

        if (since is not null)
        {
            conditions.Add("r.last_seen_utc >= $sinceUtc");
            command.Parameters.AddWithValue("$sinceUtc", since.Value.UtcDateTime.ToString("O"));
        }

        var eventJoin = string.Empty;
        var lastSeenColumn = "r.last_seen_utc";
        var openCountColumn = "r.open_count";
        var estimatedColumn = "0";
        if (eventRange is not null)
        {
            eventJoin = """
                INNER JOIN (
                    SELECT recent_item_id,
                           MAX(opened_utc) AS range_last_seen_utc,
                           COUNT(*) AS range_open_count,
                           MAX(is_estimated) AS contains_estimated
                    FROM open_events
                    WHERE opened_utc >= $rangeStartUtc AND opened_utc < $rangeEndUtc
                    GROUP BY recent_item_id
                ) AS range_events ON range_events.recent_item_id = r.id
                """;
            lastSeenColumn = "range_events.range_last_seen_utc";
            openCountColumn = "range_events.range_open_count";
            estimatedColumn = "range_events.contains_estimated";
            command.Parameters.AddWithValue("$rangeStartUtc", eventRange.StartUtc.UtcDateTime.ToString("O"));
            command.Parameters.AddWithValue("$rangeEndUtc", eventRange.EndUtc.UtcDateTime.ToString("O"));
        }

        var where = conditions.Count == 0 ? string.Empty : $"WHERE {string.Join(" AND ", conditions)}";
        command.CommandText = $"""
            SELECT r.id, r.target_path, r.display_name, r.extension, r.item_kind, r.source_link_path,
                   r.first_seen_utc, {lastSeenColumn}, {openCountColumn}, r.exists_flag,
                   {estimatedColumn}
            FROM recent_items AS r
            {eventJoin}
            {where}
            ORDER BY {lastSeenColumn} DESC
            LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", limit);

        var results = new List<RecentItem>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            results.Add(new RecentItem(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.GetString(5),
                DateTimeOffset.Parse(reader.GetString(6), CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind),
                DateTimeOffset.Parse(reader.GetString(7), CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind),
                reader.GetInt32(8),
                reader.GetInt32(9) == 1,
                reader.GetInt32(10) == 1));
        }

        return results;
    }

    private static string NormalizeFolderFilterPath(string folderPath)
    {
        try
        {
            var fullPath = Path.GetFullPath(folderPath);
            var root = Path.GetPathRoot(fullPath);
            var trimmedPath = fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return trimmedPath.Length == 0 ? root ?? fullPath : trimmedPath;
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or
                                   PathTooLongException or System.Security.SecurityException)
        {
            throw new ArgumentException("폴더 필터 경로가 유효하지 않습니다.", nameof(folderPath), ex);
        }
    }

    private static async Task<HistoryStats> GetStatsCoreAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT unique_items, total_open_count FROM history_stats WHERE id = 1;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        await reader.ReadAsync(cancellationToken);
        return new HistoryStats(reader.GetInt64(0), reader.GetInt64(1));
    }

    private static SqliteCommand CreateUpsertCommand(SqliteConnection connection,
        SqliteTransaction? transaction = null)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = UpsertSql;
        command.Parameters.Add("$targetPath", SqliteType.Text);
        command.Parameters.Add("$displayName", SqliteType.Text);
        command.Parameters.Add("$extension", SqliteType.Text);
        command.Parameters.Add("$itemKind", SqliteType.Text);
        command.Parameters.Add("$sourceLinkPath", SqliteType.Text);
        command.Parameters.Add("$seenUtc", SqliteType.Text);
        command.Parameters.Add("$linkWriteUtc", SqliteType.Text);
        command.Parameters.Add("$exists", SqliteType.Integer);
        command.Prepare();
        return command;
    }

    private static SqliteCommand CreateOpenEventCommand(
        SqliteConnection connection,
        SqliteTransaction transaction)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO open_events (
                recent_item_id, opened_utc, source_link_path, is_estimated)
            VALUES ($itemId, $openedUtc, $sourceLinkPath, 0)
            ON CONFLICT(recent_item_id, opened_utc) DO UPDATE SET
                source_link_path = excluded.source_link_path,
                is_estimated = 0;
            """;
        command.Parameters.Add("$itemId", SqliteType.Integer);
        command.Parameters.Add("$openedUtc", SqliteType.Text);
        command.Parameters.Add("$sourceLinkPath", SqliteType.Text);
        command.Prepare();
        return command;
    }

    private static async Task InsertOpenEventAsync(
        SqliteCommand command,
        long itemId,
        RecentItemCandidate item,
        CancellationToken cancellationToken)
    {
        command.Parameters["$itemId"].Value = itemId;
        command.Parameters["$openedUtc"].Value = item.LinkWriteTime.UtcDateTime.ToString("O");
        command.Parameters["$sourceLinkPath"].Value = item.SourceLinkPath;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void BindUpsertParameters(SqliteCommand command, RecentItemCandidate item)
    {
        command.Parameters["$targetPath"].Value = item.TargetPath;
        command.Parameters["$displayName"].Value = item.DisplayName;
        command.Parameters["$extension"].Value = item.Extension;
        command.Parameters["$itemKind"].Value = item.ItemKind;
        command.Parameters["$sourceLinkPath"].Value = item.SourceLinkPath;
        command.Parameters["$seenUtc"].Value = item.LinkWriteTime.UtcDateTime.ToString("O");
        command.Parameters["$linkWriteUtc"].Value = item.LinkWriteTime.UtcDateTime.ToString("O");
        command.Parameters["$exists"].Value = item.Exists ? 1 : 0;
    }

    private static async Task<bool> EnsureSearchIndexAsync(SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var existsCommand = connection.CreateCommand();
            existsCommand.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name='recent_items_fts';";
            var alreadyExists = Convert.ToInt32(await existsCommand.ExecuteScalarAsync(cancellationToken)) == 1;

            await using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE VIRTUAL TABLE IF NOT EXISTS recent_items_fts USING fts5(
                    display_name,
                    target_path,
                    content='recent_items',
                    content_rowid='id',
                    tokenize='trigram'
                );

                CREATE TRIGGER IF NOT EXISTS recent_items_fts_ai AFTER INSERT ON recent_items BEGIN
                    INSERT INTO recent_items_fts(rowid, display_name, target_path)
                    VALUES (new.id, new.display_name, new.target_path);
                END;

                CREATE TRIGGER IF NOT EXISTS recent_items_fts_ad AFTER DELETE ON recent_items BEGIN
                    INSERT INTO recent_items_fts(recent_items_fts, rowid, display_name, target_path)
                    VALUES ('delete', old.id, old.display_name, old.target_path);
                END;

                DROP TRIGGER IF EXISTS recent_items_fts_au;

                CREATE TRIGGER recent_items_fts_au
                AFTER UPDATE OF display_name, target_path ON recent_items
                WHEN old.display_name <> new.display_name OR old.target_path <> new.target_path
                BEGIN
                    INSERT INTO recent_items_fts(recent_items_fts, rowid, display_name, target_path)
                    VALUES ('delete', old.id, old.display_name, old.target_path);
                    INSERT INTO recent_items_fts(rowid, display_name, target_path)
                    VALUES (new.id, new.display_name, new.target_path);
                END;
                """;
            await command.ExecuteNonQueryAsync(cancellationToken);

            if (!alreadyExists)
            {
                await using var rebuildCommand = connection.CreateCommand();
                rebuildCommand.CommandText = "INSERT INTO recent_items_fts(recent_items_fts) VALUES('rebuild');";
                await rebuildCommand.ExecuteNonQueryAsync(cancellationToken);
            }
            return true;
        }
        catch (SqliteException)
        {
            return false;
        }
    }

    private static async Task<bool> IsSelectiveFtsQueryAsync(SqliteConnection connection, string ftsQuery,
        int limit, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT COUNT(*)
            FROM (
                SELECT rowid
                FROM recent_items_fts
                WHERE recent_items_fts MATCH $ftsProbe
                LIMIT $probeLimit
            );
            """;
        command.Parameters.AddWithValue("$ftsProbe", ftsQuery);
        command.Parameters.AddWithValue("$probeLimit", limit + 1);
        var matchCount = Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
        return matchCount <= limit;
    }

    private static string? BuildFtsQuery(IReadOnlyCollection<string> tokens)
    {
        if (tokens.Count == 0 || tokens.Any(token =>
                token.Length < 3 || token.Contains('*') || token.Contains('?')))
        {
            return null;
        }

        return string.Join(" AND ", tokens.Select(token =>
            $"\"{token.Replace("\"", "\"\"", StringComparison.Ordinal)}\""));
    }

    private static string BuildLikePattern(string token)
    {
        var usesWildcard = token.Contains('*') || token.Contains('?');
        var pattern = new StringBuilder(token.Length + 2);
        if (!usesWildcard)
        {
            pattern.Append('%');
        }

        foreach (var character in token)
        {
            switch (character)
            {
                case '\\':
                    pattern.Append("\\\\");
                    break;
                case '%':
                    pattern.Append("\\%");
                    break;
                case '_':
                    pattern.Append("\\_");
                    break;
                case '*' when usesWildcard:
                    pattern.Append('%');
                    break;
                case '?' when usesWildcard:
                    pattern.Append('_');
                    break;
                default:
                    pattern.Append(character);
                    break;
            }
        }

        if (!usesWildcard)
        {
            pattern.Append('%');
        }
        return pattern.ToString();
    }

    private static bool TryGetExactExtensionPattern(string token, out string extension)
    {
        extension = string.Empty;
        if (!token.StartsWith("*.", StringComparison.Ordinal) || token.Length <= 2)
        {
            return false;
        }

        var candidate = token[2..];
        if (candidate.Any(character => character is '*' or '?' or '.' or '\\' or '/'))
        {
            return false;
        }

        extension = candidate;
        return true;
    }
}
