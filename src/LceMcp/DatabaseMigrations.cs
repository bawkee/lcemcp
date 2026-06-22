namespace LceMcp;

internal static class DatabaseMigrations
{
    public static IReadOnlyList<DatabaseMigration> All { get; } =
    [
        new(1, "initial_metadata_cache", InitialSchemaSql),
        new(2, "message_bodies_and_search", BodySearchSchemaSql),
        new(3, "sync_runs_and_search_readiness", SyncRunsAndReadinessSql),
        new(4, "sync_queue_and_leases", SyncQueueAndLeasesSql),
        new(5, "sync_window_tracking", SyncWindowTrackingSql)
    ];

    public static int TargetVersion => All.Select(migration => migration.Version).DefaultIfEmpty(0).Max();

    public const string InitialSchemaSql = """
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
            sync_enabled INTEGER NOT NULL DEFAULT 0,
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

        CREATE TABLE IF NOT EXISTS messages (
            id INTEGER PRIMARY KEY,
            account_id INTEGER NOT NULL,
            provider_message_key TEXT,
            provider_thread_key TEXT,
            message_id_header TEXT,
            in_reply_to TEXT,
            references_header TEXT,
            thread_key TEXT,
            subject TEXT,
            normalized_subject TEXT,
            from_name TEXT,
            from_email TEXT,
            date_sent TEXT,
            date_received TEXT,
            has_attachments INTEGER NOT NULL DEFAULT 0,
            size_bytes INTEGER,
            raw_headers TEXT,
            body_downloaded INTEGER NOT NULL DEFAULT 0,
            created_at TEXT NOT NULL,
            updated_at TEXT NOT NULL,
            FOREIGN KEY(account_id) REFERENCES accounts(id) ON DELETE CASCADE
        );

        CREATE TABLE IF NOT EXISTS message_locations (
            id INTEGER PRIMARY KEY,
            message_id INTEGER NOT NULL,
            account_id INTEGER NOT NULL,
            folder_id INTEGER NOT NULL,
            provider_uid TEXT NOT NULL,
            provider_message_key TEXT,
            flags TEXT,
            labels TEXT,
            deleted_locally INTEGER NOT NULL DEFAULT 0,
            expunged INTEGER NOT NULL DEFAULT 0,
            first_seen_at TEXT NOT NULL,
            last_seen_at TEXT NOT NULL,
            FOREIGN KEY(message_id) REFERENCES messages(id) ON DELETE CASCADE,
            FOREIGN KEY(account_id) REFERENCES accounts(id) ON DELETE CASCADE,
            FOREIGN KEY(folder_id) REFERENCES folders(id) ON DELETE CASCADE,
            UNIQUE(account_id, folder_id, provider_uid)
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
        CREATE INDEX IF NOT EXISTS idx_messages_account_date ON messages(account_id, date_sent);
        CREATE INDEX IF NOT EXISTS idx_messages_provider_key ON messages(account_id, provider_message_key);
        CREATE INDEX IF NOT EXISTS idx_messages_message_id_header ON messages(account_id, message_id_header);
        CREATE INDEX IF NOT EXISTS idx_messages_thread_key ON messages(account_id, thread_key);
        CREATE INDEX IF NOT EXISTS idx_message_locations_message ON message_locations(message_id);
        CREATE INDEX IF NOT EXISTS idx_message_locations_folder_uid ON message_locations(folder_id, provider_uid);
        CREATE INDEX IF NOT EXISTS idx_audit_log_timestamp ON audit_log(timestamp);
        """;

    public const string BodySearchSchemaSql = """
        CREATE TABLE IF NOT EXISTS message_bodies (
            message_id INTEGER PRIMARY KEY,
            plain_text TEXT,
            html_text TEXT,
            normalized_text TEXT,
            detected_language TEXT,
            normalized_at TEXT,
            FOREIGN KEY(message_id) REFERENCES messages(id) ON DELETE CASCADE
        );

        CREATE TABLE IF NOT EXISTS message_recipients (
            id INTEGER PRIMARY KEY,
            message_id INTEGER NOT NULL,
            type TEXT NOT NULL,
            name TEXT,
            email TEXT,
            FOREIGN KEY(message_id) REFERENCES messages(id) ON DELETE CASCADE
        );

        CREATE TABLE IF NOT EXISTS message_search_docs (
            message_id INTEGER PRIMARY KEY,
            subject TEXT,
            from_email TEXT,
            from_name TEXT,
            recipients TEXT,
            body TEXT,
            FOREIGN KEY(message_id) REFERENCES messages(id) ON DELETE CASCADE
        );

        CREATE VIRTUAL TABLE IF NOT EXISTS messages_fts USING fts5(
            subject,
            from_email,
            from_name,
            recipients,
            body,
            content='message_search_docs',
            content_rowid='message_id',
            tokenize='unicode61 remove_diacritics 2'
        );

        CREATE TRIGGER IF NOT EXISTS message_search_docs_ai AFTER INSERT ON message_search_docs BEGIN
            INSERT INTO messages_fts(rowid, subject, from_email, from_name, recipients, body)
            VALUES (new.message_id, new.subject, new.from_email, new.from_name, new.recipients, new.body);
        END;

        CREATE TRIGGER IF NOT EXISTS message_search_docs_ad AFTER DELETE ON message_search_docs BEGIN
            INSERT INTO messages_fts(messages_fts, rowid, subject, from_email, from_name, recipients, body)
            VALUES('delete', old.message_id, old.subject, old.from_email, old.from_name, old.recipients, old.body);
        END;

        CREATE TRIGGER IF NOT EXISTS message_search_docs_au AFTER UPDATE ON message_search_docs BEGIN
            INSERT INTO messages_fts(messages_fts, rowid, subject, from_email, from_name, recipients, body)
            VALUES('delete', old.message_id, old.subject, old.from_email, old.from_name, old.recipients, old.body);
            INSERT INTO messages_fts(rowid, subject, from_email, from_name, recipients, body)
            VALUES (new.message_id, new.subject, new.from_email, new.from_name, new.recipients, new.body);
        END;

        CREATE INDEX IF NOT EXISTS idx_message_recipients_message ON message_recipients(message_id);
        CREATE INDEX IF NOT EXISTS idx_message_recipients_email ON message_recipients(email);
        """;

    public const string SyncRunsAndReadinessSql = """
        ALTER TABLE accounts ADD COLUMN history_days INTEGER NOT NULL DEFAULT 90;

        CREATE TABLE IF NOT EXISTS sync_runs (
            id TEXT PRIMARY KEY,
            account_id INTEGER,
            account_name TEXT NOT NULL,
            folder_filter TEXT,
            status TEXT NOT NULL,
            phase TEXT NOT NULL,
            done INTEGER NOT NULL DEFAULT 0,
            total INTEGER NOT NULL DEFAULT 0,
            started_at TEXT NOT NULL,
            last_progress_at TEXT NOT NULL,
            completed_at TEXT,
            last_error TEXT,
            FOREIGN KEY(account_id) REFERENCES accounts(id) ON DELETE SET NULL
        );

        CREATE INDEX IF NOT EXISTS idx_sync_runs_status_progress ON sync_runs(status, last_progress_at);
        CREATE INDEX IF NOT EXISTS idx_sync_runs_account_progress ON sync_runs(account_id, last_progress_at);
        """;

    public const string SyncQueueAndLeasesSql = """
        ALTER TABLE sync_runs ADD COLUMN scope_key TEXT NOT NULL DEFAULT 'global';
        ALTER TABLE sync_runs ADD COLUMN requested_at TEXT;
        ALTER TABLE sync_runs ADD COLUMN owner_id TEXT;

        UPDATE sync_runs
        SET requested_at = started_at
        WHERE requested_at IS NULL;

        CREATE TABLE IF NOT EXISTS sync_leases (
            scope_key TEXT PRIMARY KEY,
            sync_run_id TEXT NOT NULL,
            owner_id TEXT NOT NULL,
            heartbeat_at TEXT NOT NULL,
            lease_expires_at TEXT NOT NULL,
            FOREIGN KEY(sync_run_id) REFERENCES sync_runs(id) ON DELETE CASCADE
        );

        CREATE INDEX IF NOT EXISTS idx_sync_runs_scope_status ON sync_runs(scope_key, status, requested_at);
        CREATE INDEX IF NOT EXISTS idx_sync_leases_expires ON sync_leases(lease_expires_at);
        """;

    public const string SyncWindowTrackingSql = """
        ALTER TABLE sync_runs ADD COLUMN requested_since_days INTEGER;
        ALTER TABLE sync_runs ADD COLUMN effective_since_days INTEGER;
        ALTER TABLE sync_runs ADD COLUMN auto_expanded_for_gap INTEGER NOT NULL DEFAULT 0;
        """;
}
