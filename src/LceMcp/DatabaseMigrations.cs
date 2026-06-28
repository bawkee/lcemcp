namespace LceMcp;

internal static class DatabaseMigrations
{
    public static IReadOnlyList<DatabaseMigration> All { get; } =
    [
        new(1, "initial_metadata_cache", InitialSchemaSql),
        new(2, "message_bodies_and_search", BodySearchSchemaSql),
        new(3, "sync_runs_and_search_readiness", SyncRunsAndReadinessSql),
        new(4, "sync_queue_and_leases", SyncQueueAndLeasesSql),
        new(5, "sync_window_tracking", SyncWindowTrackingSql),
        new(6, "attachment_metadata_and_search", AttachmentSearchSchemaSql),
        new(7, "attachment_scan_tracking", AttachmentScanTrackingSql),
        new(8, "attachment_processing_reliability", AttachmentProcessingReliabilitySql),
        new(9, "bounded_body_retries", BoundedBodyRetriesSql)
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

    public const string AttachmentSearchSchemaSql = """
        CREATE TABLE IF NOT EXISTS attachments (
            id INTEGER PRIMARY KEY,
            message_id INTEGER NOT NULL,
            parent_attachment_id INTEGER,
            root_attachment_id INTEGER,
            source_kind TEXT NOT NULL DEFAULT 'email_part',
            part_id TEXT,
            filename TEXT,
            display_path TEXT NOT NULL,
            archive_entry_path TEXT,
            mime_type TEXT,
            sniffed_mime_type TEXT,
            size_bytes INTEGER,
            compressed_size_bytes INTEGER,
            uncompressed_size_bytes INTEGER,
            content_hash TEXT,
            storage_key TEXT,
            is_container INTEGER NOT NULL DEFAULT 0,
            nesting_depth INTEGER NOT NULL DEFAULT 0,
            download_status TEXT NOT NULL DEFAULT 'not_downloaded',
            download_attempts INTEGER NOT NULL DEFAULT 0,
            download_error TEXT,
            extraction_status TEXT NOT NULL DEFAULT 'not_ready',
            extraction_attempts INTEGER NOT NULL DEFAULT 0,
            extraction_started_at TEXT,
            extraction_lease_until TEXT,
            extraction_error TEXT,
            extracted_text_available INTEGER NOT NULL DEFAULT 0,
            ocr_text_available INTEGER NOT NULL DEFAULT 0,
            created_at TEXT NOT NULL,
            updated_at TEXT NOT NULL,
            FOREIGN KEY(message_id) REFERENCES messages(id) ON DELETE CASCADE,
            FOREIGN KEY(parent_attachment_id) REFERENCES attachments(id) ON DELETE CASCADE,
            FOREIGN KEY(root_attachment_id) REFERENCES attachments(id) ON DELETE CASCADE,
            UNIQUE(message_id, source_kind, display_path)
        );

        CREATE TABLE IF NOT EXISTS attachment_text (
            attachment_id INTEGER PRIMARY KEY,
            extracted_text TEXT,
            ocr_text TEXT,
            combined_text TEXT,
            extractor TEXT,
            extracted_at TEXT,
            FOREIGN KEY(attachment_id) REFERENCES attachments(id) ON DELETE CASCADE
        );

        CREATE TABLE IF NOT EXISTS attachment_search_docs (
            attachment_id INTEGER PRIMARY KEY,
            filename TEXT,
            display_path TEXT,
            mime_type TEXT,
            extracted_text TEXT,
            FOREIGN KEY(attachment_id) REFERENCES attachments(id) ON DELETE CASCADE
        );

        CREATE VIRTUAL TABLE IF NOT EXISTS attachments_fts USING fts5(
            filename,
            display_path,
            mime_type,
            extracted_text,
            content='attachment_search_docs',
            content_rowid='attachment_id',
            tokenize='unicode61 remove_diacritics 2'
        );

        CREATE TRIGGER IF NOT EXISTS attachment_search_docs_ai AFTER INSERT ON attachment_search_docs BEGIN
            INSERT INTO attachments_fts(rowid, filename, display_path, mime_type, extracted_text)
            VALUES (new.attachment_id, new.filename, new.display_path, new.mime_type, new.extracted_text);
        END;

        CREATE TRIGGER IF NOT EXISTS attachment_search_docs_ad AFTER DELETE ON attachment_search_docs BEGIN
            INSERT INTO attachments_fts(attachments_fts, rowid, filename, display_path, mime_type, extracted_text)
            VALUES('delete', old.attachment_id, old.filename, old.display_path, old.mime_type, old.extracted_text);
        END;

        CREATE TRIGGER IF NOT EXISTS attachment_search_docs_au AFTER UPDATE ON attachment_search_docs BEGIN
            INSERT INTO attachments_fts(attachments_fts, rowid, filename, display_path, mime_type, extracted_text)
            VALUES('delete', old.attachment_id, old.filename, old.display_path, old.mime_type, old.extracted_text);
            INSERT INTO attachments_fts(rowid, filename, display_path, mime_type, extracted_text)
            VALUES (new.attachment_id, new.filename, new.display_path, new.mime_type, new.extracted_text);
        END;

        CREATE INDEX IF NOT EXISTS idx_attachments_message ON attachments(message_id);
        CREATE INDEX IF NOT EXISTS idx_attachments_parent ON attachments(parent_attachment_id);
        CREATE INDEX IF NOT EXISTS idx_attachments_root ON attachments(root_attachment_id);
        CREATE INDEX IF NOT EXISTS idx_attachments_hash ON attachments(content_hash);
        CREATE INDEX IF NOT EXISTS idx_attachments_display_path ON attachments(message_id, display_path);
        CREATE INDEX IF NOT EXISTS idx_attachments_extraction_status ON attachments(extraction_status, extraction_lease_until);
        CREATE INDEX IF NOT EXISTS idx_attachments_mime ON attachments(sniffed_mime_type, mime_type);
        """;

    public const string AttachmentScanTrackingSql = """
        ALTER TABLE messages ADD COLUMN attachments_scanned INTEGER NOT NULL DEFAULT 0;

        CREATE INDEX IF NOT EXISTS idx_messages_attachment_scan
        ON messages(account_id, has_attachments, attachments_scanned);
        """;

    public const string AttachmentProcessingReliabilitySql = """
        ALTER TABLE attachments ADD COLUMN download_started_at TEXT;
        ALTER TABLE attachments ADD COLUMN download_lease_until TEXT;
        ALTER TABLE attachments ADD COLUMN download_next_attempt_at TEXT;
        ALTER TABLE attachments ADD COLUMN download_completed_at TEXT;
        ALTER TABLE attachments ADD COLUMN download_error_code TEXT;
        ALTER TABLE attachments ADD COLUMN extraction_next_attempt_at TEXT;
        ALTER TABLE attachments ADD COLUMN extraction_completed_at TEXT;
        ALTER TABLE attachments ADD COLUMN extraction_error_code TEXT;
        ALTER TABLE attachments ADD COLUMN extractor TEXT;
        ALTER TABLE attachments ADD COLUMN extractor_version TEXT;
        ALTER TABLE attachments ADD COLUMN extraction_lease_token TEXT;

        ALTER TABLE messages ADD COLUMN body_attempts INTEGER NOT NULL DEFAULT 0;
        ALTER TABLE messages ADD COLUMN body_next_attempt_at TEXT;
        ALTER TABLE messages ADD COLUMN body_last_error_code TEXT;
        ALTER TABLE messages ADD COLUMN body_last_error TEXT;

        CREATE TABLE attachment_extraction_attempts (
            id INTEGER PRIMARY KEY,
            attachment_id INTEGER NOT NULL,
            stage TEXT NOT NULL DEFAULT 'extraction',
            trigger_kind TEXT NOT NULL,
            client_name TEXT,
            extractor TEXT,
            extractor_version TEXT,
            started_at TEXT NOT NULL,
            completed_at TEXT,
            outcome TEXT NOT NULL,
            error_code TEXT,
            exception_type TEXT,
            exception_message TEXT,
            exception_details TEXT,
            FOREIGN KEY(attachment_id) REFERENCES attachments(id) ON DELETE CASCADE
        );

        CREATE TABLE attachment_extraction_failures (
            id INTEGER PRIMARY KEY,
            attachment_id INTEGER NOT NULL,
            stage TEXT NOT NULL,
            error_code TEXT NOT NULL,
            status TEXT NOT NULL DEFAULT 'open',
            first_attempt_id INTEGER NOT NULL,
            latest_attempt_id INTEGER NOT NULL,
            occurrence_count INTEGER NOT NULL DEFAULT 1,
            first_seen_at TEXT NOT NULL,
            last_checked_at TEXT NOT NULL,
            resolved_at TEXT,
            resolved_by_attempt_id INTEGER,
            FOREIGN KEY(attachment_id) REFERENCES attachments(id) ON DELETE CASCADE,
            FOREIGN KEY(first_attempt_id) REFERENCES attachment_extraction_attempts(id),
            FOREIGN KEY(latest_attempt_id) REFERENCES attachment_extraction_attempts(id),
            FOREIGN KEY(resolved_by_attempt_id) REFERENCES attachment_extraction_attempts(id)
        );

        CREATE UNIQUE INDEX idx_attachment_extraction_failures_open
        ON attachment_extraction_failures(attachment_id, stage, error_code)
        WHERE status = 'open';

        CREATE INDEX idx_attachment_extraction_failures_status
        ON attachment_extraction_failures(status, error_code, last_checked_at);

        CREATE INDEX idx_attachment_extraction_attempts_attachment
        ON attachment_extraction_attempts(attachment_id, started_at);

        UPDATE attachments
        SET
            extraction_error_code = CASE extraction_status
                WHEN 'unsupported' THEN 'unsupported_attachment_type'
                WHEN 'encrypted' THEN 'encrypted_document'
                WHEN 'too_large' THEN 'attachment_too_large'
                WHEN 'failed' THEN CASE
                    WHEN storage_key IS NULL THEN 'temporary_io_failure'
                    ELSE 'unknown_extractor_failure'
                END
            END,
            extractor = (
                SELECT attachment_text.extractor
                FROM attachment_text
                WHERE attachment_text.attachment_id = attachments.id
            ),
            extraction_completed_at = CASE
                WHEN extraction_status IN ('done', 'empty', 'unsupported', 'encrypted', 'too_large', 'failed')
                THEN updated_at
            END
        WHERE extraction_status IN ('done', 'empty', 'unsupported', 'encrypted', 'too_large', 'failed');

        INSERT INTO attachment_extraction_attempts (
            attachment_id,
            stage,
            trigger_kind,
            extractor,
            started_at,
            completed_at,
            outcome,
            error_code,
            exception_message
        )
        SELECT
            id,
            'extraction',
            'legacy_migration',
            extractor,
            COALESCE(extraction_started_at, updated_at, created_at),
            COALESCE(extraction_completed_at, updated_at, created_at),
            CASE WHEN extraction_error_code IS NULL THEN extraction_status ELSE 'failed' END,
            extraction_error_code,
            extraction_error
        FROM attachments
        WHERE extraction_status IN ('done', 'empty', 'unsupported', 'encrypted', 'too_large', 'failed');

        INSERT INTO attachment_extraction_failures (
            attachment_id,
            stage,
            error_code,
            status,
            first_attempt_id,
            latest_attempt_id,
            occurrence_count,
            first_seen_at,
            last_checked_at
        )
        SELECT
            a.id,
            'extraction',
            a.extraction_error_code,
            'open',
            (
                SELECT MAX(attempt.id)
                FROM attachment_extraction_attempts attempt
                WHERE attempt.attachment_id = a.id
            ),
            (
                SELECT MAX(attempt.id)
                FROM attachment_extraction_attempts attempt
                WHERE attempt.attachment_id = a.id
            ),
            1,
            COALESCE(a.extraction_completed_at, a.updated_at, a.created_at),
            COALESCE(a.extraction_completed_at, a.updated_at, a.created_at)
        FROM attachments a
        WHERE a.extraction_error_code IS NOT NULL;

        UPDATE attachments
        SET extraction_status = 'failed'
        WHERE extraction_status IN ('unsupported', 'encrypted', 'too_large');

        CREATE INDEX idx_attachments_extraction_schedule
        ON attachments(extraction_status, extraction_next_attempt_at, extraction_lease_until);

        CREATE INDEX idx_messages_body_retry
        ON messages(account_id, body_downloaded, attachments_scanned, body_next_attempt_at, body_attempts);
        """;

    public const string BoundedBodyRetriesSql = """
        ALTER TABLE messages ADD COLUMN body_retry_exhausted INTEGER NOT NULL DEFAULT 0;
        ALTER TABLE attachment_extraction_failures ADD COLUMN error_summary TEXT;

        UPDATE attachment_extraction_failures
        SET error_summary = CASE error_code
            WHEN 'unsupported_attachment_type' THEN 'No text extractor is available for this attachment type.'
            WHEN 'invalid_document' THEN 'The attachment is invalid or corrupt.'
            WHEN 'encrypted_document' THEN 'The attachment is encrypted and cannot be extracted.'
            WHEN 'extractor_timeout' THEN 'Attachment extraction timed out.'
            WHEN 'extractor_unavailable' THEN 'The attachment extractor was temporarily unavailable.'
            WHEN 'temporary_io_failure' THEN 'A temporary I/O error interrupted attachment processing.'
            WHEN 'worker_canceled' THEN 'Attachment extraction was canceled.'
            WHEN 'worker_crashed' THEN 'The attachment extraction worker stopped before completion.'
            WHEN 'attachment_too_large' THEN 'The attachment exceeded the configured size limit.'
            WHEN 'archive_safety_limit' THEN 'The archive was rejected by a safety limit.'
            ELSE 'Attachment processing failed unexpectedly.'
        END;

        CREATE INDEX idx_messages_body_retry_exhausted
        ON messages(account_id, body_retry_exhausted, body_next_attempt_at, body_attempts);
        """;
}
