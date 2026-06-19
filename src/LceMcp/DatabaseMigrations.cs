namespace LceMcp;

internal static class DatabaseMigrations
{
    public static IReadOnlyList<DatabaseMigration> All { get; } =
    [
        new(1, "initial_metadata_cache", InitialSchemaSql)
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
}
