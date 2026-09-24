using System.Globalization;
using FindHistory.Models;
using Microsoft.Data.Sqlite;

namespace FindHistory.Services;

public sealed class RecentDatabase : IDisposable
{
    private string _connectionString;
    private string _databasePath;
    private bool _ftsAvailable;
    private readonly SemaphoreSlim _databaseGate = new(1, 1);

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
            exists_flag = excluded.exists_flag;
        """;

    public RecentDatabase(string databasePath)
    {
        _databasePath = Path.GetFullPath(databasePath);
        _connectionString = BuildConnectionString(_databasePath);
    }

    public string DatabasePath => _databasePath;

    public void Dispose() => _databaseGate.Dispose();

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
            """;
        await command.ExecuteNonQueryAsync(cancellationToken);
        _ftsAvailable = await EnsureSearchIndexAsync(connection, cancellationToken);
    }

    public async Task UpsertAsync(RecentItemCandidate item)
    {
        await _databaseGate.WaitAsync();
        try
        {
            await using var connection = await OpenAsync();
            await using var command = CreateUpsertCommand(connection);
            BindUpsertParameters(command, item);
            await command.ExecuteNonQueryAsync();
        }
        finally
        {
            _databaseGate.Release();
        }
    }

    public async Task UpsertManyAsync(IReadOnlyCollection<RecentItemCandidate> items,
        CancellationToken cancellationToken = default)
    {
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
            foreach (var item in items)
            {
                BindUpsertParameters(command, item);
                await command.ExecuteNonQueryAsync(cancellationToken);
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
        await _databaseGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();

            var conditions = new List<string>();
            var tokens = searchText.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var ftsQuery = BuildFtsQuery(tokens);
            var useFts = ftsQuery is not null && _ftsAvailable &&
                         await IsSelectiveFtsQueryAsync(connection, ftsQuery, limit, cancellationToken);
            if (useFts)
            {
                conditions.Add("id IN (SELECT rowid FROM recent_items_fts WHERE recent_items_fts MATCH $ftsQuery)");
                command.Parameters.AddWithValue("$ftsQuery", ftsQuery!);
            }
            else
            {
                for (var i = 0; i < tokens.Length; i++)
                {
                    conditions.Add($"(display_name LIKE $query{i} ESCAPE '\\' OR target_path LIKE $query{i} ESCAPE '\\')");
                    command.Parameters.AddWithValue($"$query{i}", $"%{EscapeLike(tokens[i])}%");
                }
            }

            if (since is not null)
            {
                conditions.Add("last_seen_utc >= $sinceUtc");
                command.Parameters.AddWithValue("$sinceUtc", since.Value.UtcDateTime.ToString("O"));
            }

            var where = conditions.Count == 0 ? string.Empty : $"WHERE {string.Join(" AND ", conditions)}";
            command.CommandText = $"""
            SELECT id, target_path, display_name, extension, item_kind, source_link_path,
                   first_seen_utc, last_seen_utc, open_count, exists_flag
            FROM recent_items
            {where}
            ORDER BY last_seen_utc DESC
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
                    reader.GetInt32(9) == 1));
            }

            return results;
        }
        finally
        {
            _databaseGate.Release();
        }
    }

    public async Task<HistoryStats> GetStatsAsync(CancellationToken cancellationToken = default)
    {
        await _databaseGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*), COALESCE(SUM(open_count), 0) FROM recent_items;";
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            await reader.ReadAsync(cancellationToken);
            return new HistoryStats(reader.GetInt64(0), reader.GetInt64(1));
        }
        finally
        {
            _databaseGate.Release();
        }
    }

    public async Task MoveToAsync(string destinationPath, CancellationToken cancellationToken = default)
    {
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
        var targetPath = Path.GetFullPath(databasePath);
        if (!File.Exists(targetPath))
        {
            throw new FileNotFoundException("선택한 데이터베이스 파일을 찾을 수 없습니다.", targetPath);
        }

        await _databaseGate.WaitAsync(cancellationToken);
        try
        {
            await ValidateFindHistoryDatabaseAsync(targetPath, cancellationToken);
            _databasePath = targetPath;
            _connectionString = BuildConnectionString(_databasePath);
            await InitializeCoreAsync(cancellationToken);
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

                CREATE TRIGGER IF NOT EXISTS recent_items_fts_au AFTER UPDATE ON recent_items BEGIN
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
        if (tokens.Count == 0 || tokens.Any(token => token.Length < 3))
        {
            return null;
        }

        return string.Join(" AND ", tokens.Select(token =>
            $"\"{token.Replace("\"", "\"\"", StringComparison.Ordinal)}\""));
    }

    private static string EscapeLike(string value) => value
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("%", "\\%", StringComparison.Ordinal)
        .Replace("_", "\\_", StringComparison.Ordinal);
}
