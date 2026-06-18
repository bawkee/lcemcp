using Microsoft.Data.Sqlite;

namespace LceMcp;

internal sealed class EmailDatabase
{
    private readonly AppPaths _paths;

    public EmailDatabase(AppPaths paths)
    {
        _paths = paths;
    }

    public string DatabasePath => _paths.DatabasePath;

    public DatabaseInitializationKind EnsureInitialized()
    {
        _paths.EnsureDataDirectories();

        return DatabaseMigrations.MigrationsLocked
            ? EnsurePrototypeDatabase()
            : ApplyMigrations();
    }

    public DatabaseStatus GetStatus()
    {
        var initializationKind = EnsureInitialized();

        using var connection = OpenConnection();

        return new DatabaseStatus(
            SchemaMode: DatabaseMigrations.MigrationsLocked ? "prototype-reset" : "migrations",
            MigrationsLocked: DatabaseMigrations.MigrationsLocked,
            SchemaVersion: ExecuteScalarInt(connection, "SELECT COALESCE(MAX(version), 0) FROM schema_migrations;"),
            TargetSchemaVersion: DatabaseMigrations.TargetVersion,
            AccountCount: ExecuteScalarInt(connection, "SELECT COUNT(*) FROM accounts;"),
            FolderCount: ExecuteScalarInt(connection, "SELECT COUNT(*) FROM folders;"),
            LastSyncState: ReadLastSyncState(connection),
            InitializationKind: initializationKind);
    }

    public int UpsertConfiguredAccount(AccountConfig account)
    {
        EnsureInitialized();

        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();

        var accountId = UpsertConfiguredAccount(connection, transaction, account, DateTimeOffset.UtcNow.ToString("O"));
        transaction.Commit();
        return accountId;
    }

    public IReadOnlyList<int> UpsertConfiguredAccounts(IEnumerable<AccountConfig> accounts)
    {
        EnsureInitialized();

        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();

        var now = DateTimeOffset.UtcNow.ToString("O");
        var accountIds = new List<int>();

        foreach (var account in accounts)
            accountIds.Add(UpsertConfiguredAccount(connection, transaction, account, now));

        transaction.Commit();
        return accountIds;
    }

    public int UpsertFolders(int accountId, IReadOnlyCollection<ImapFolderInfo> folders)
    {
        EnsureInitialized();

        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();

        var now = DateTimeOffset.UtcNow.ToString("O");

        foreach (var folder in folders)
            UpsertFolder(connection, transaction, accountId, folder, now);

        transaction.Commit();
        return folders.Count;
    }

    public IReadOnlyList<StoredFolder> ReadFolders(string accountFilter)
    {
        EnsureInitialized();

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();

        command.CommandText = string.IsNullOrWhiteSpace(accountFilter)
            ? """
                SELECT
                    f.id,
                    f.account_id,
                    a.name AS account_name,
                    a.email_address AS account_email_address,
                    f.name,
                    f.path,
                    f.delimiter,
                    f.attributes,
                    f.role,
                    f.selectable,
                    f.sync_enabled,
                    f.uidvalidity,
                    f.message_count,
                    f.recent_count,
                    f.last_discovered_at
                FROM folders f
                JOIN accounts a ON a.id = f.account_id
                ORDER BY a.name COLLATE NOCASE, f.path COLLATE NOCASE;
                """
            : """
                SELECT
                    f.id,
                    f.account_id,
                    a.name AS account_name,
                    a.email_address AS account_email_address,
                    f.name,
                    f.path,
                    f.delimiter,
                    f.attributes,
                    f.role,
                    f.selectable,
                    f.sync_enabled,
                    f.uidvalidity,
                    f.message_count,
                    f.recent_count,
                    f.last_discovered_at
                FROM folders f
                JOIN accounts a ON a.id = f.account_id
                WHERE a.name COLLATE NOCASE = $accountFilter
                   OR a.email_address COLLATE NOCASE = $accountFilter
                   OR CAST(a.id AS TEXT) = $accountFilter
                ORDER BY a.name COLLATE NOCASE, f.path COLLATE NOCASE;
                """;

        if (!string.IsNullOrWhiteSpace(accountFilter))
            command.Parameters.AddWithValue("$accountFilter", accountFilter.Trim());

        using var reader = command.ExecuteReader();
        var folders = new List<StoredFolder>();

        while (reader.Read())
        {
            folders.Add(new(
                Id: reader.GetInt32(reader.GetOrdinal("id")),
                AccountId: reader.GetInt32(reader.GetOrdinal("account_id")),
                AccountName: reader.GetString(reader.GetOrdinal("account_name")),
                AccountEmailAddress: reader.GetString(reader.GetOrdinal("account_email_address")),
                Name: reader.GetString(reader.GetOrdinal("name")),
                Path: reader.GetString(reader.GetOrdinal("path")),
                Delimiter: GetNullableString(reader, "delimiter"),
                Attributes: GetNullableString(reader, "attributes"),
                Role: reader.GetString(reader.GetOrdinal("role")),
                Selectable: reader.GetInt32(reader.GetOrdinal("selectable")) != 0,
                SyncEnabled: reader.GetInt32(reader.GetOrdinal("sync_enabled")) != 0,
                UidValidity: GetNullableString(reader, "uidvalidity"),
                MessageCount: GetNullableInt(reader, "message_count"),
                RecentCount: GetNullableInt(reader, "recent_count"),
                LastDiscoveredAt: GetNullableString(reader, "last_discovered_at")));
        }

        return folders;
    }

    private DatabaseInitializationKind EnsurePrototypeDatabase()
    {
        if (DatabaseMigrations.All.Count > 0)
        {
            throw new CliException(
                "Database migrations are locked for the prototype phase. Leave DatabaseMigrations.All empty, or remove the lock when the MVP schema is ready to preserve.",
                2);
        }

        var databaseExisted = File.Exists(_paths.DatabasePath);
        var needsRebuild = databaseExisted && !HasCurrentPrototypeMarker();

        if (needsRebuild)
            DeleteDatabaseFiles();

        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();

        ExecuteNonQuery(connection, transaction, MigrationTableSql);
        ExecuteNonQuery(connection, transaction, PrototypeSchemaSql);
        ExecuteNonQuery(connection, transaction, "DELETE FROM schema_migrations;");

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO schema_migrations (version, name, applied_at)
            VALUES ($version, $name, $appliedAt);
            """;
        command.Parameters.AddWithValue("$version", DatabaseMigrations.PrototypeSchemaVersion);
        command.Parameters.AddWithValue("$name", DatabaseMigrations.PrototypeSchemaName);
        command.Parameters.AddWithValue("$appliedAt", DateTimeOffset.UtcNow.ToString("O"));
        command.ExecuteNonQuery();

        transaction.Commit();

        if (needsRebuild)
            return DatabaseInitializationKind.RecreatedPrototype;

        return databaseExisted
            ? DatabaseInitializationKind.Opened
            : DatabaseInitializationKind.Created;
    }

    private DatabaseInitializationKind ApplyMigrations()
    {
        var migrations = DatabaseMigrations.All
            .OrderBy(migration => migration.Version)
            .ToList();

        ValidateMigrations(migrations);

        var databaseExisted = File.Exists(_paths.DatabasePath);

        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();

        ExecuteNonQuery(connection, transaction, MigrationTableSql);

        var appliedMigrations = ReadAppliedMigrations(connection, transaction);
        ValidateAppliedMigrations(appliedMigrations, migrations);

        var pendingMigrations = migrations
            .Where(migration => !appliedMigrations.ContainsKey(migration.Version))
            .ToList();

        foreach (var migration in pendingMigrations)
        {
            ExecuteNonQuery(connection, transaction, migration.Sql);
            InsertMigrationRow(connection, transaction, migration);
        }

        transaction.Commit();

        if (!databaseExisted)
            return DatabaseInitializationKind.Created;

        return pendingMigrations.Count == 0
            ? DatabaseInitializationKind.Opened
            : DatabaseInitializationKind.Migrated;
    }

    private bool HasCurrentPrototypeMarker()
    {
        try
        {
            using var connection = OpenConnection();
            if (!TableExists(connection, "schema_migrations"))
                return false;

            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT name
                FROM schema_migrations
                WHERE version = $version;
                """;
            command.Parameters.AddWithValue("$version", DatabaseMigrations.PrototypeSchemaVersion);

            var name = command.ExecuteScalar() as string;
            return string.Equals(name, DatabaseMigrations.PrototypeSchemaName, StringComparison.Ordinal);
        }
        catch (SqliteException)
        {
            return false;
        }
    }

    private void DeleteDatabaseFiles()
    {
        foreach (var path in new[]
        {
            _paths.DatabasePath,
            _paths.DatabasePath + "-wal",
            _paths.DatabasePath + "-shm"
        })
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }

    private SqliteConnection OpenConnection()
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _paths.DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ToString();

        var connection = new SqliteConnection(connectionString);
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA foreign_keys = ON;";
        command.ExecuteNonQuery();

        return connection;
    }

    private static int ExecuteScalarInt(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = command.ExecuteScalar();
        return Convert.ToInt32(value);
    }

    private static int UpsertConfiguredAccount(
        SqliteConnection connection,
        SqliteTransaction transaction,
        AccountConfig account,
        string now)
    {
        var existingAccountId = FindExistingAccountId(connection, transaction, account);

        if (existingAccountId is int accountId)
        {
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE accounts
                SET
                    name = $name,
                    email_address = $emailAddress,
                    provider_preset = $providerPreset,
                    imap_host = $imapHost,
                    imap_port = $imapPort,
                    imap_security = $imapSecurity,
                    username = $username,
                    auth_type = $authType,
                    credential_ref = $credentialRef,
                    enabled = $enabled
                WHERE id = $id;
                """;
            AddParameter(command, "$id", accountId);
            AddAccountParameters(command, account);
            command.ExecuteNonQuery();
            return accountId;
        }

        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO accounts (
                    name,
                    email_address,
                    provider_preset,
                    imap_host,
                    imap_port,
                    imap_security,
                    username,
                    auth_type,
                    credential_ref,
                    created_at,
                    enabled
                )
                VALUES (
                    $name,
                    $emailAddress,
                    $providerPreset,
                    $imapHost,
                    $imapPort,
                    $imapSecurity,
                    $username,
                    $authType,
                    $credentialRef,
                    $createdAt,
                    $enabled
                );
                """;
            AddAccountParameters(command, account);
            AddParameter(command, "$createdAt", now);
            command.ExecuteNonQuery();
        }

        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT last_insert_rowid();";
            return Convert.ToInt32(command.ExecuteScalar());
        }
    }

    private static int? FindExistingAccountId(
        SqliteConnection connection,
        SqliteTransaction transaction,
        AccountConfig account)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT id
            FROM accounts
            WHERE name COLLATE NOCASE = $name
               OR email_address COLLATE NOCASE = $emailAddress
            ORDER BY CASE WHEN name COLLATE NOCASE = $name THEN 0 ELSE 1 END
            LIMIT 1;
            """;
        AddParameter(command, "$name", account.Id);
        AddParameter(command, "$emailAddress", account.EmailAddress);

        var value = command.ExecuteScalar();
        return value is null ? null : Convert.ToInt32(value);
    }

    private static void AddAccountParameters(SqliteCommand command, AccountConfig account)
    {
        AddParameter(command, "$name", account.Id);
        AddParameter(command, "$emailAddress", account.EmailAddress);
        AddParameter(command, "$providerPreset", BlankToNull(account.Provider));
        AddParameter(command, "$imapHost", account.ImapHost);
        AddParameter(command, "$imapPort", account.ImapPort);
        AddParameter(command, "$imapSecurity", account.ImapSecurity);
        AddParameter(command, "$username", account.Username);
        AddParameter(command, "$authType", "password");
        AddParameter(command, "$credentialRef", BlankToNull(account.CredentialRef));
        AddParameter(command, "$enabled", account.Enabled ? 1 : 0);
    }

    private static void UpsertFolder(
        SqliteConnection connection,
        SqliteTransaction transaction,
        int accountId,
        ImapFolderInfo folder,
        string now)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO folders (
                account_id,
                name,
                path,
                delimiter,
                attributes,
                role,
                selectable,
                uidvalidity,
                message_count,
                recent_count,
                last_discovered_at
            )
            VALUES (
                $accountId,
                $name,
                $path,
                $delimiter,
                $attributes,
                $role,
                $selectable,
                $uidValidity,
                $messageCount,
                $recentCount,
                $lastDiscoveredAt
            )
            ON CONFLICT(account_id, path) DO UPDATE SET
                name = excluded.name,
                delimiter = excluded.delimiter,
                attributes = excluded.attributes,
                role = excluded.role,
                selectable = excluded.selectable,
                uidvalidity = excluded.uidvalidity,
                message_count = excluded.message_count,
                recent_count = excluded.recent_count,
                last_discovered_at = excluded.last_discovered_at;
            """;
        AddParameter(command, "$accountId", accountId);
        AddParameter(command, "$name", folder.Name);
        AddParameter(command, "$path", folder.FullName);
        AddParameter(command, "$delimiter", folder.Delimiter);
        AddParameter(command, "$attributes", folder.Attributes);
        AddParameter(command, "$role", folder.Role);
        AddParameter(command, "$selectable", folder.Selectable ? 1 : 0);
        AddParameter(command, "$uidValidity", folder.UidValidity);
        AddParameter(command, "$messageCount", folder.MessageCount);
        AddParameter(command, "$recentCount", folder.RecentCount);
        AddParameter(command, "$lastDiscoveredAt", now);
        command.ExecuteNonQuery();
    }

    private static void AddParameter(SqliteCommand command, string name, object value)
    {
        command.Parameters.AddWithValue(name, value ?? DBNull.Value);
    }

    private static string BlankToNull(string value) =>
        string.IsNullOrWhiteSpace(value) ? null : value;

    private static bool TableExists(SqliteConnection connection, string tableName)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT 1
            FROM sqlite_master
            WHERE type = 'table' AND name = $tableName
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$tableName", tableName);

        return command.ExecuteScalar() is not null;
    }

    private static void ExecuteNonQuery(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static Dictionary<int, string> ReadAppliedMigrations(
        SqliteConnection connection,
        SqliteTransaction transaction)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT version, name FROM schema_migrations ORDER BY version;";

        using var reader = command.ExecuteReader();
        var migrations = new Dictionary<int, string>();

        while (reader.Read())
            migrations.Add(reader.GetInt32(0), reader.GetString(1));

        return migrations;
    }

    private static void InsertMigrationRow(
        SqliteConnection connection,
        SqliteTransaction transaction,
        DatabaseMigration migration)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO schema_migrations (version, name, applied_at)
            VALUES ($version, $name, $appliedAt);
            """;
        command.Parameters.AddWithValue("$version", migration.Version);
        command.Parameters.AddWithValue("$name", migration.Name);
        command.Parameters.AddWithValue("$appliedAt", DateTimeOffset.UtcNow.ToString("O"));
        command.ExecuteNonQuery();
    }

    private static void ValidateMigrations(IReadOnlyList<DatabaseMigration> migrations)
    {
        if (migrations.Count == 0)
            throw new CliException("Database migrations are enabled, but no migrations are defined.", 2);

        for (var i = 0; i < migrations.Count; i++)
        {
            var expectedVersion = i + 1;
            if (migrations[i].Version != expectedVersion)
                throw new CliException($"Database migrations must be contiguous starting at 1; expected version {expectedVersion}.", 2);

            if (string.IsNullOrWhiteSpace(migrations[i].Name))
                throw new CliException($"Database migration {migrations[i].Version} has no name.", 2);

            if (string.IsNullOrWhiteSpace(migrations[i].Sql))
                throw new CliException($"Database migration {migrations[i].Version} has no SQL.", 2);
        }
    }

    private static void ValidateAppliedMigrations(
        IReadOnlyDictionary<int, string> appliedMigrations,
        IReadOnlyList<DatabaseMigration> migrations)
    {
        var knownMigrations = migrations.ToDictionary(migration => migration.Version);

        foreach (var applied in appliedMigrations)
        {
            if (!knownMigrations.TryGetValue(applied.Key, out var expected))
                throw new CliException($"Database has unknown migration version {applied.Key}; refusing to continue.", 2);

            if (!string.Equals(applied.Value, expected.Name, StringComparison.Ordinal))
            {
                throw new CliException(
                    $"Database migration {applied.Key} is named '{applied.Value}', but code expects '{expected.Name}'.",
                    2);
            }
        }
    }

    private static SyncStateStatus ReadLastSyncState(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                a.name AS account_name,
                f.path AS folder_path,
                s.last_success_at,
                s.last_error_at,
                s.last_error
            FROM sync_state s
            JOIN accounts a ON a.id = s.account_id
            JOIN folders f ON f.id = s.folder_id
            ORDER BY COALESCE(s.last_success_at, s.last_error_at, '') DESC
            LIMIT 1;
            """;

        using var reader = command.ExecuteReader();
        if (!reader.Read())
            return null;

        return new SyncStateStatus(
            AccountName: reader.GetString(reader.GetOrdinal("account_name")),
            FolderPath: reader.GetString(reader.GetOrdinal("folder_path")),
            LastSuccessAt: GetNullableString(reader, "last_success_at"),
            LastErrorAt: GetNullableString(reader, "last_error_at"),
            LastError: GetNullableString(reader, "last_error"));
    }

    private static string GetNullableString(SqliteDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? null : reader.GetString(ordinal);
    }

    private static int? GetNullableInt(SqliteDataReader reader, string name)
    {
        var ordinal = reader.GetOrdinal(name);
        return reader.IsDBNull(ordinal) ? null : reader.GetInt32(ordinal);
    }

    private const string MigrationTableSql = """
        CREATE TABLE IF NOT EXISTS schema_migrations (
            version INTEGER PRIMARY KEY,
            name TEXT NOT NULL,
            applied_at TEXT NOT NULL
        );
        """;

    private const string PrototypeSchemaSql = """
        CREATE TABLE IF NOT EXISTS accounts (
            id INTEGER PRIMARY KEY,
            name TEXT NOT NULL,
            email_address TEXT NOT NULL,
            provider_preset TEXT,
            imap_host TEXT NOT NULL,
            imap_port INTEGER NOT NULL,
            imap_security TEXT NOT NULL DEFAULT 'ssl',
            smtp_host TEXT,
            smtp_port INTEGER,
            smtp_security TEXT,
            username TEXT NOT NULL,
            auth_type TEXT NOT NULL DEFAULT 'password',
            credential_ref TEXT,
            created_at TEXT NOT NULL,
            last_sync_at TEXT,
            enabled INTEGER NOT NULL DEFAULT 1,
            UNIQUE(name),
            UNIQUE(email_address)
        );

        CREATE TABLE IF NOT EXISTS folders (
            id INTEGER PRIMARY KEY,
            account_id INTEGER NOT NULL,
            name TEXT NOT NULL,
            path TEXT NOT NULL,
            delimiter TEXT,
            attributes TEXT,
            role TEXT NOT NULL DEFAULT 'custom',
            selectable INTEGER NOT NULL DEFAULT 1,
            sync_enabled INTEGER NOT NULL DEFAULT 1,
            uidvalidity TEXT,
            message_count INTEGER,
            recent_count INTEGER,
            last_uid INTEGER,
            last_discovered_at TEXT,
            last_sync_at TEXT,
            FOREIGN KEY(account_id) REFERENCES accounts(id) ON DELETE CASCADE,
            UNIQUE(account_id, path)
        );

        CREATE TABLE IF NOT EXISTS sync_state (
            id INTEGER PRIMARY KEY,
            account_id INTEGER NOT NULL,
            folder_id INTEGER NOT NULL,
            state_json TEXT,
            last_success_at TEXT,
            last_error_at TEXT,
            last_error TEXT,
            FOREIGN KEY(account_id) REFERENCES accounts(id) ON DELETE CASCADE,
            FOREIGN KEY(folder_id) REFERENCES folders(id) ON DELETE CASCADE,
            UNIQUE(account_id, folder_id)
        );

        CREATE TABLE IF NOT EXISTS audit_log (
            id INTEGER PRIMARY KEY,
            timestamp TEXT NOT NULL,
            client_name TEXT,
            tool_name TEXT NOT NULL,
            action_type TEXT NOT NULL,
            arguments_summary TEXT,
            affected_message_ids TEXT,
            affected_attachment_ids TEXT,
            affected_draft_ids TEXT,
            result_summary TEXT
        );

        CREATE INDEX IF NOT EXISTS idx_folders_account_role ON folders(account_id, role);
        CREATE INDEX IF NOT EXISTS idx_sync_state_last_success ON sync_state(last_success_at);
        CREATE INDEX IF NOT EXISTS idx_audit_log_timestamp ON audit_log(timestamp);
        """;
}
