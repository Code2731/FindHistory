using System.Globalization;
using System.Text;
using FindHistory.Models;
using Microsoft.Data.Sqlite;

namespace FindHistory.Services;

public sealed class RecentDatabase : IDisposable
{
    private const int CurrentSchemaVersion = 1;
    private string _connectionString;
    private string _databasePath;
    private bool _ftsAvailable;
    private readonly SemaphoreSlim _databaseGate = new(1, 1);
    private int _disposeState;

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
    }

    public string DatabasePath => _databasePath;

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) != 0)
        {
            return;
        }

        // 진행 중인 읽기/쓰기가 gate를 반환한 뒤에만 SemaphoreSlim을 폐기한다.
        _databaseGate.Wait();
        _databaseGate.Release();
        _databaseGate.Dispose();
    }

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
                total_open_count INTEGER NOT NULL
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

            INSERT OR IGNORE INTO history_stats (id, unique_items, total_open_count)
            SELECT 1, COUNT(*), COALESCE(SUM(open_count), 0) FROM recent_items;

            PRAGMA user_version = 1;
            """;
        await migration.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
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
                    (SELECT COUNT(*) FROM open_events),
                    (SELECT COUNT(*) FROM open_events WHERE is_estimated = 1)
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
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT opened_utc, is_estimated
                FROM open_events
                WHERE opened_utc >= $rangeStartUtc AND opened_utc < $rangeEndUtc
                ORDER BY opened_utc;
                """;
            command.Parameters.AddWithValue(
                "$rangeStartUtc", dateRange.StartUtc.UtcDateTime.ToString("O"));
            command.Parameters.AddWithValue(
                "$rangeEndUtc", dateRange.EndUtc.UtcDateTime.ToString("O"));

            var zone = timeZone ?? TimeZoneInfo.Local;
            var totals = new Dictionary<DateTime, (int Count, bool ContainsEstimated)>();
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var openedUtc = DateTimeOffset.Parse(
                    reader.GetString(0), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
                var localDate = TimeZoneInfo.ConvertTime(openedUtc, zone).Date;
                totals.TryGetValue(localDate, out var total);
                totals[localDate] = (
                    total.Count + 1,
                    total.ContainsEstimated || reader.GetInt32(1) == 1);
            }

            return totals
                .OrderBy(pair => pair.Key)
                .Select(pair => new DailyActivity(
                    pair.Key, pair.Value.Count, pair.Value.ContainsEstimated))
                .ToArray();
        }
        finally
        {
            _databaseGate.Release();
        }
    }

    public async Task MoveToAsync(string destinationPath, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        var targetPath = Path.GetFullPath(destinationPath);
        await _databaseGate.WaitAsync(cancellationToken);
        try
        {
            if (string.Equals(targetPath, _databasePath, StringComparison.OrdinalIgnoreCase))
            {
                return;
            }
            if (File.Exists(targetPath))
            {
                throw new IOException("선택한 위치에 findhistory.db가 이미 있습니다.");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            await CheckpointAsync(cancellationToken);

            var sourcePath = _databasePath;
            File.Move(sourcePath, targetPath);
            DeleteCheckpointSidecar(sourcePath + "-wal");
            DeleteCheckpointSidecar(sourcePath + "-shm");

            _databasePath = targetPath;
            _connectionString = BuildConnectionString(_databasePath);
            await InitializeCoreAsync(cancellationToken);
        }
        finally
        {
            _databaseGate.Release();
        }
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
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken cancellationToken = default)
    {
        var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        return connection;
    }

    private void ThrowIfDisposed() => ObjectDisposedException.ThrowIf(
        Volatile.Read(ref _disposeState) != 0, this);

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
            var folder = Path.GetFullPath(filters.Folder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            // 디렉터리 구분자까지 포함해 Work와 Work-old를 구분한다. LIKE 메타문자는 리터럴 처리한다.
            var prefix = folder + Path.DirectorySeparatorChar;
            var escapedPrefix = prefix.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_");
            conditions.Add("r.target_path LIKE $folderPrefix ESCAPE '\\'");
            command.Parameters.AddWithValue("$folderPrefix", escapedPrefix + "%");
        }
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
