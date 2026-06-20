using Microsoft.Data.Sqlite;
using SqlBinder;
using SqlBinder.Parsing;
using System.Text.Json;

namespace LceMcp;

internal sealed class EmailDatabase
{
    private readonly AppPaths _paths;
    private const string GlobalSyncScopeKey = "global";

    public EmailDatabase(AppPaths paths)
    {
        _paths = paths;
    }

    public string DatabasePath => _paths.DatabasePath;

    public DatabaseInitializationKind EnsureInitialized()
    {
        _paths.EnsureDataDirectories();
        return ApplyMigrations();
    }

    public DatabaseStatus GetStatus()
    {
        var initializationKind = EnsureInitialized();

        using var connection = OpenConnection();

        return new DatabaseStatus(
            SchemaVersion: ExecuteScalarInt(connection, "SELECT COALESCE(MAX(version), 0) FROM schema_migrations;"),
            TargetSchemaVersion: DatabaseMigrations.TargetVersion,
            AccountCount: ExecuteScalarInt(connection, "SELECT COUNT(*) FROM accounts;"),
            FolderCount: ExecuteScalarInt(connection, "SELECT COUNT(*) FROM folders;"),
            MessageCount: ExecuteScalarInt(connection, "SELECT COUNT(*) FROM messages;"),
            MessageLocationCount: ExecuteScalarInt(connection, "SELECT COUNT(*) FROM message_locations;"),
            MessageBodyCount: ExecuteScalarInt(connection, "SELECT COUNT(*) FROM message_bodies;"),
            MessageSearchDocCount: ExecuteScalarInt(connection, "SELECT COUNT(*) FROM message_search_docs;"),
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
                    f.last_discovered_at,
                    f.last_sync_at
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
                    f.last_discovered_at,
                    f.last_sync_at
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
                LastDiscoveredAt: GetNullableString(reader, "last_discovered_at"),
                LastSyncAt: GetNullableString(reader, "last_sync_at")));
        }

        return folders;
    }

    public IReadOnlyList<StoredFolder> ReadSyncFolders(string accountFilter, string folderFilter)
    {
        var folders = ReadFolders(accountFilter)
            .Where(folder => folder.Selectable && folder.SyncEnabled);

        if (!string.IsNullOrWhiteSpace(folderFilter))
        {
            var requested = folderFilter.Trim();
            folders = folders.Where(folder =>
                folder.Path.Equals(requested, StringComparison.OrdinalIgnoreCase)
                || folder.Name.Equals(requested, StringComparison.OrdinalIgnoreCase)
                || folder.Id.ToString().Equals(requested, StringComparison.OrdinalIgnoreCase));
        }

        return folders
            .OrderBy(folder => folder.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public AccountSyncSummary ReadAccountSyncSummary(string accountFilter)
    {
        EnsureInitialized();

        if (string.IsNullOrWhiteSpace(accountFilter))
            return null;

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                a.id,
                a.name,
                a.email_address,
                (
                    SELECT s.last_success_at
                    FROM sync_state s
                    WHERE s.account_id = a.id
                      AND s.last_success_at IS NOT NULL
                    ORDER BY s.last_success_at DESC
                    LIMIT 1
                ) AS last_success_at,
                (
                    SELECT s.last_error_at
                    FROM sync_state s
                    WHERE s.account_id = a.id
                      AND s.last_error_at IS NOT NULL
                    ORDER BY s.last_error_at DESC
                    LIMIT 1
                ) AS last_error_at,
                (
                    SELECT s.last_error
                    FROM sync_state s
                    WHERE s.account_id = a.id
                      AND s.last_error_at IS NOT NULL
                    ORDER BY s.last_error_at DESC
                    LIMIT 1
                ) AS last_error
            FROM accounts a
            WHERE a.name COLLATE NOCASE = $accountFilter
               OR a.email_address COLLATE NOCASE = $accountFilter
               OR CAST(a.id AS TEXT) = $accountFilter
            ORDER BY CASE WHEN a.name COLLATE NOCASE = $accountFilter THEN 0 ELSE 1 END
            LIMIT 1;
            """;
        AddParameter(command, "$accountFilter", accountFilter.Trim());

        using var reader = command.ExecuteReader();
        if (!reader.Read())
            return null;

        return new(
            AccountId: reader.GetInt32(reader.GetOrdinal("id")),
            AccountName: reader.GetString(reader.GetOrdinal("name")),
            EmailAddress: reader.GetString(reader.GetOrdinal("email_address")),
            LastSuccessAt: GetNullableString(reader, "last_success_at"),
            LastErrorAt: GetNullableString(reader, "last_error_at"),
            LastError: GetNullableString(reader, "last_error"));
    }

    public void WriteAuditLog(
        string clientName,
        string toolName,
        string actionType,
        string argumentsSummary,
        string resultSummary,
        IReadOnlyList<int> affectedMessageIds = null)
    {
        EnsureInitialized();

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO audit_log (
                timestamp,
                client_name,
                tool_name,
                action_type,
                arguments_summary,
                affected_message_ids,
                affected_attachment_ids,
                affected_draft_ids,
                result_summary
            )
            VALUES (
                $timestamp,
                $clientName,
                $toolName,
                $actionType,
                $argumentsSummary,
                $affectedMessageIds,
                NULL,
                NULL,
                $resultSummary
            );
            """;
        AddParameter(command, "$timestamp", DateTimeOffset.UtcNow.ToString("O"));
        AddParameter(command, "$clientName", BlankToNull(clientName));
        AddParameter(command, "$toolName", toolName);
        AddParameter(command, "$actionType", actionType);
        AddParameter(command, "$argumentsSummary", BlankToNull(argumentsSummary));
        AddParameter(command, "$affectedMessageIds", affectedMessageIds is { Count: > 0 } ? string.Join(",", affectedMessageIds) : null);
        AddParameter(command, "$resultSummary", BlankToNull(resultSummary));
        command.ExecuteNonQuery();
    }

    public IReadOnlyList<BodySyncTarget> ReadPendingBodySyncTargets(
        string accountFilter,
        string folderFilter,
        int maxPerFolder)
    {
        EnsureInitialized();

        var folders = ReadSyncFolders(accountFilter, folderFilter);
        if (folders.Count == 0)
            return [];

        using var connection = OpenConnection();
        var targets = new List<BodySyncTarget>();
        var limit = maxPerFolder > 0 ? maxPerFolder : int.MaxValue;

        foreach (var folder in folders)
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT
                    m.id AS message_id,
                    ml.folder_id,
                    f.path AS folder_path,
                    ml.provider_uid,
                    m.subject
                FROM message_locations ml
                JOIN messages m ON m.id = ml.message_id
                JOIN folders f ON f.id = ml.folder_id
                WHERE ml.folder_id = $folderId
                  AND m.body_downloaded = 0
                  AND ml.deleted_locally = 0
                  AND ml.expunged = 0
                ORDER BY COALESCE(m.date_sent, m.date_received, '') DESC,
                         CAST(ml.provider_uid AS INTEGER) DESC
                LIMIT $limit;
                """;
            AddParameter(command, "$folderId", folder.Id);
            AddParameter(command, "$limit", limit);

            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                targets.Add(new(
                    MessageId: reader.GetInt32(reader.GetOrdinal("message_id")),
                    FolderId: reader.GetInt32(reader.GetOrdinal("folder_id")),
                    FolderPath: reader.GetString(reader.GetOrdinal("folder_path")),
                    ProviderUid: reader.GetString(reader.GetOrdinal("provider_uid")),
                    Subject: GetNullableString(reader, "subject")));
            }
        }

        return targets;
    }

    public void UpsertMessageBody(MessageBodyContent body)
    {
        EnsureInitialized();

        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();

        var now = DateTimeOffset.UtcNow.ToString("O");

        UpsertMessageBody(connection, transaction, body, now);
        transaction.Commit();
    }

    public IReadOnlyList<EmailSearchResult> SearchMessages(EmailSearchRequest request)
    {
        EnsureInitialized();

        var fts = FtsQueryBuilder.Build(request.Query);
        var limit = Math.Clamp(request.Limit <= 0 ? 20 : request.Limit, 1, 101);
        var snippetTokens = Math.Clamp(request.SnippetChars <= 0 ? 32 : request.SnippetChars / 6, 16, 64);

        using var connection = OpenConnection();
        var accountIds = ResolveAccountIds(connection, request.AccountFilters);

        if (HasValues(request.AccountFilters) && accountIds.Count == 0)
            return [];

        var query = CreateDbQuery(connection, SearchMessagesSql);
        ApplyScopeConditions(query, accountIds, request.FromEmail, request.FolderRoles, request.HasAttachment);

        using var command = query.CreateCommand();
        query.AddSqlParameter("$fts", fts);
        query.AddSqlParameter("$limit", limit);
        query.AddSqlParameter("$snippetTokens", snippetTokens);

        using var reader = (SqliteDataReader)command.ExecuteReader();
        var results = new List<EmailSearchResult>();

        while (reader.Read())
        {
            results.Add(new(
                MessageId: reader.GetInt32(reader.GetOrdinal("message_id")),
                AccountName: reader.GetString(reader.GetOrdinal("account_name")),
                Folders: GetNullableString(reader, "folders"),
                Date: GetNullableString(reader, "message_date"),
                FromName: GetNullableString(reader, "from_name"),
                FromEmail: GetNullableString(reader, "from_email"),
                Subject: GetNullableString(reader, "subject"),
                HasAttachments: reader.GetInt32(reader.GetOrdinal("has_attachments")) != 0,
                Snippet: GetNullableString(reader, "snippet"),
                Score: reader.GetDouble(reader.GetOrdinal("score"))));
        }

        return results;
    }

    public StoredEmailMessage ReadMessage(int messageId)
    {
        EnsureInitialized();

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                m.id AS message_id,
                a.name AS account_name,
                a.email_address AS account_email_address,
                m.date_sent,
                m.date_received,
                m.from_name,
                m.from_email,
                m.subject,
                m.has_attachments,
                b.normalized_text AS body_text
            FROM messages m
            JOIN accounts a ON a.id = m.account_id
            LEFT JOIN message_bodies b ON b.message_id = m.id
            WHERE m.id = $messageId
            LIMIT 1;
            """;
        AddParameter(command, "$messageId", messageId);

        int storedMessageId;
        string accountName;
        string accountEmailAddress;
        string dateSent;
        string dateReceived;
        string fromName;
        string fromEmail;
        string subject;
        bool hasAttachments;
        string bodyText;

        using (var reader = command.ExecuteReader())
        {
            if (!reader.Read())
                return null;

            storedMessageId = reader.GetInt32(reader.GetOrdinal("message_id"));
            accountName = reader.GetString(reader.GetOrdinal("account_name"));
            accountEmailAddress = reader.GetString(reader.GetOrdinal("account_email_address"));
            dateSent = GetNullableString(reader, "date_sent");
            dateReceived = GetNullableString(reader, "date_received");
            fromName = GetNullableString(reader, "from_name");
            fromEmail = GetNullableString(reader, "from_email");
            subject = GetNullableString(reader, "subject");
            hasAttachments = reader.GetInt32(reader.GetOrdinal("has_attachments")) != 0;
            bodyText = GetNullableString(reader, "body_text");
        }

        return new(
            MessageId: storedMessageId,
            AccountName: accountName,
            AccountEmailAddress: accountEmailAddress,
            DateSent: dateSent,
            DateReceived: dateReceived,
            FromName: fromName,
            FromEmail: fromEmail,
            Subject: subject,
            HasAttachments: hasAttachments,
            BodyText: bodyText,
            Folders: ReadMessageFolders(connection, messageId),
            Recipients: ReadMessageRecipients(connection, messageId));
    }

    public MessageSearchReadiness GetMessageSearchReadiness(MessageSearchReadinessRequest request)
    {
        EnsureInitialized();

        using var connection = OpenConnection();
        var accountIds = ResolveAccountIds(connection, request.AccountFilters);
        ReconcileExpiredSyncWork(connection, DateTimeOffset.UtcNow);
        var activeSyncRun = ReadActiveSyncRun(connection);

        if (HasValues(request.AccountFilters) && accountIds.Count == 0)
        {
            return new(
                SearchReady: false,
                MetadataComplete: false,
                BodiesComplete: false,
                MessageSearchIndexComplete: false,
                ScopeAccountCount: 0,
                ScopeFolderCount: 0,
                MetadataCompleteFolderCount: 0,
                MetadataMessages: 0,
                IndexedMessageBodies: 0,
                MessageSearchDocs: 0,
                FtsRows: 0,
                PendingMessageBodies: 0,
                ActiveSyncRun: activeSyncRun);
        }

        var scopedAccountCount = ReadScopedAccountCount(connection, accountIds, request.AccountFilters);
        var folderStates = ReadMessageSearchFolderStates(connection, accountIds, request.FolderRoles);
        var counts = ReadMessageSearchCounts(connection, accountIds, request);
        var completeFolderCount = folderStates.Count(IsFolderMetadataComplete);

        var metadataComplete = scopedAccountCount > 0
            && folderStates.Count > 0
            && completeFolderCount == folderStates.Count;
        var bodiesComplete = counts.MetadataMessages == counts.IndexedMessageBodies
            && counts.PendingMessageBodies == 0;
        var indexComplete = counts.MetadataMessages == counts.MessageSearchDocs
            && counts.MetadataMessages == counts.FtsRows;

        return new(
            SearchReady: metadataComplete && bodiesComplete && indexComplete,
            MetadataComplete: metadataComplete,
            BodiesComplete: bodiesComplete,
            MessageSearchIndexComplete: indexComplete,
            ScopeAccountCount: scopedAccountCount,
            ScopeFolderCount: folderStates.Count,
            MetadataCompleteFolderCount: completeFolderCount,
            MetadataMessages: counts.MetadataMessages,
            IndexedMessageBodies: counts.IndexedMessageBodies,
            MessageSearchDocs: counts.MessageSearchDocs,
            FtsRows: counts.FtsRows,
            PendingMessageBodies: counts.PendingMessageBodies,
            ActiveSyncRun: activeSyncRun);
    }

    public SyncRunStartResult StartOrQueueSyncRun(
        int? accountId,
        string accountName,
        string folderFilter,
        string phase,
        int total)
    {
        EnsureInitialized();

        using var connection = OpenConnection();
        var ownerId = BuildSyncOwnerId();

        return RunInImmediateTransaction(connection, () =>
        {
            var now = DateTimeOffset.UtcNow;
            ReconcileExpiredSyncWork(connection, now);

            var hasActiveLease = HasActiveSyncLease(connection, GlobalSyncScopeKey, now);
            var hasQueuedRun = HasQueuedSyncRun(connection, GlobalSyncScopeKey);
            var status = hasActiveLease || hasQueuedRun ? "queued" : "running";
            var syncRunId = InsertSyncRun(
                connection,
                accountId,
                accountName,
                folderFilter,
                phase,
                Math.Max(0, total),
                ownerId,
                GlobalSyncScopeKey,
                status,
                now);

            if (status == "running")
                InsertSyncLease(connection, GlobalSyncScopeKey, syncRunId, ownerId, now);

            return new SyncRunStartResult(
                syncRunId,
                GlobalSyncScopeKey,
                ownerId,
                status,
                Acquired: status == "running",
                ActiveRun: ReadActiveSyncRun(connection));
        });
    }

    public SyncRunStartResult TryClaimQueuedSyncRun(string syncRunId, string ownerId)
    {
        EnsureInitialized();

        using var connection = OpenConnection();

        return RunInImmediateTransaction(connection, () =>
        {
            var now = DateTimeOffset.UtcNow;
            ReconcileExpiredSyncWork(connection, now);
            HeartbeatQueuedSyncRun(connection, syncRunId, ownerId, now);

            var run = ReadSyncRunSnapshot(connection, syncRunId);
            if (run is null)
            {
                return new SyncRunStartResult(
                    syncRunId,
                    GlobalSyncScopeKey,
                    ownerId,
                    "missing",
                    Acquired: false,
                    ActiveRun: ReadActiveSyncRun(connection));
            }

            if (!run.Status.Equals("queued", StringComparison.OrdinalIgnoreCase))
            {
                return new SyncRunStartResult(
                    syncRunId,
                    run.ScopeKey,
                    ownerId,
                    run.Status,
                    Acquired: HasMatchingSyncLease(connection, run.ScopeKey, syncRunId, ownerId, now),
                    ActiveRun: ReadActiveSyncRun(connection));
            }

            if (HasActiveSyncLease(connection, run.ScopeKey, now)
                || !IsOldestQueuedSyncRun(connection, run.ScopeKey, syncRunId))
            {
                return new SyncRunStartResult(
                    syncRunId,
                    run.ScopeKey,
                    ownerId,
                    "queued",
                    Acquired: false,
                    ActiveRun: ReadActiveSyncRun(connection));
            }

            MarkSyncRunRunning(connection, syncRunId, ownerId, now);
            InsertSyncLease(connection, run.ScopeKey, syncRunId, ownerId, now);

            return new SyncRunStartResult(
                syncRunId,
                run.ScopeKey,
                ownerId,
                "running",
                Acquired: true,
                ActiveRun: ReadSyncRunSnapshot(connection, syncRunId));
        });
    }

    public bool HeartbeatSyncLease(string syncRunId, string ownerId)
    {
        EnsureInitialized();

        using var connection = OpenConnection();
        var now = DateTimeOffset.UtcNow;
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE sync_leases
            SET
                heartbeat_at = $heartbeatAt,
                lease_expires_at = $leaseExpiresAt
            WHERE sync_run_id = $syncRunId
              AND owner_id = $ownerId
              AND lease_expires_at >= $heartbeatAt;
            """;
        AddParameter(command, "$syncRunId", syncRunId);
        AddParameter(command, "$ownerId", ownerId);
        AddParameter(command, "$heartbeatAt", now.ToString("O"));
        AddParameter(command, "$leaseExpiresAt", now.Add(SyncLeaseDuration).ToString("O"));
        return command.ExecuteNonQuery() > 0;
    }

    public bool OwnsActiveSyncLease(string syncRunId, string ownerId)
    {
        EnsureInitialized();

        using var connection = OpenConnection();
        return HasMatchingSyncLease(connection, GlobalSyncScopeKey, syncRunId, ownerId, DateTimeOffset.UtcNow);
    }

    public bool UpdateSyncRunProgress(string syncRunId, string ownerId, int done, int total)
    {
        EnsureInitialized();

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE sync_runs
            SET
                done = $done,
                total = $total,
                last_progress_at = $lastProgressAt
            WHERE id = $id
              AND status = 'running'
              AND EXISTS (
                  SELECT 1
                  FROM sync_leases
                  WHERE sync_leases.scope_key = sync_runs.scope_key
                    AND sync_leases.sync_run_id = sync_runs.id
                    AND sync_leases.owner_id = $ownerId
                    AND sync_leases.lease_expires_at >= $lastProgressAt
              );
            """;
        AddParameter(command, "$id", syncRunId);
        AddParameter(command, "$ownerId", ownerId);
        AddParameter(command, "$done", Math.Max(0, done));
        AddParameter(command, "$total", Math.Max(0, total));
        AddParameter(command, "$lastProgressAt", DateTimeOffset.UtcNow.ToString("O"));
        return command.ExecuteNonQuery() > 0;
    }

    public bool UpdateSyncRunPhase(string syncRunId, string ownerId, string phase, int done, int total)
    {
        EnsureInitialized();

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE sync_runs
            SET
                phase = $phase,
                done = $done,
                total = $total,
                last_progress_at = $lastProgressAt
            WHERE id = $id
              AND status = 'running'
              AND EXISTS (
                  SELECT 1
                  FROM sync_leases
                  WHERE sync_leases.scope_key = sync_runs.scope_key
                    AND sync_leases.sync_run_id = sync_runs.id
                    AND sync_leases.owner_id = $ownerId
                    AND sync_leases.lease_expires_at >= $lastProgressAt
              );
            """;
        AddParameter(command, "$id", syncRunId);
        AddParameter(command, "$ownerId", ownerId);
        AddParameter(command, "$phase", string.IsNullOrWhiteSpace(phase) ? "syncing" : phase.Trim());
        AddParameter(command, "$done", Math.Max(0, done));
        AddParameter(command, "$total", Math.Max(0, total));
        AddParameter(command, "$lastProgressAt", DateTimeOffset.UtcNow.ToString("O"));
        return command.ExecuteNonQuery() > 0;
    }

    public bool CompleteSyncRun(
        string syncRunId,
        string ownerId,
        bool succeeded,
        int done,
        int total,
        string lastError)
    {
        return FinishSyncRun(syncRunId, ownerId, succeeded ? "succeeded" : "failed", done, total, lastError);
    }

    public bool CancelSyncRun(string syncRunId, string ownerId, int done, int total)
    {
        return FinishSyncRun(syncRunId, ownerId, "canceled", done, total, "canceled");
    }

    private bool FinishSyncRun(
        string syncRunId,
        string ownerId,
        string status,
        int done,
        int total,
        string lastError)
    {
        EnsureInitialized();

        using var connection = OpenConnection();
        return RunInImmediateTransaction(connection, () =>
        {
            var now = DateTimeOffset.UtcNow;
            ReconcileExpiredSyncWork(connection, now);
            var run = ReadSyncRunSnapshot(connection, syncRunId);
            if (run is null)
                return false;

            var ownsRunningLease = HasMatchingSyncLease(connection, run.ScopeKey, syncRunId, ownerId, now);
            var ownsQueuedRun = run.Status.Equals("queued", StringComparison.OrdinalIgnoreCase)
                && run.LastError is null
                && IsQueuedSyncRunOwner(connection, syncRunId, ownerId);

            if (!ownsRunningLease && !(status == "canceled" && ownsQueuedRun))
                return false;

            using (var command = connection.CreateCommand())
            {
                command.CommandText = """
                    UPDATE sync_runs
                    SET
                        status = $status,
                        done = $done,
                        total = $total,
                        last_progress_at = $lastProgressAt,
                        completed_at = $completedAt,
                        last_error = $lastError
                    WHERE id = $id
                      AND status IN ('queued', 'running');
                    """;
                AddParameter(command, "$id", syncRunId);
                AddParameter(command, "$status", status);
                AddParameter(command, "$done", Math.Max(0, done));
                AddParameter(command, "$total", Math.Max(0, total));
                AddParameter(command, "$lastProgressAt", now.ToString("O"));
                AddParameter(command, "$completedAt", now.ToString("O"));
                AddParameter(command, "$lastError", BlankToNull(lastError));
                command.ExecuteNonQuery();
            }

            using (var command = connection.CreateCommand())
            {
                command.CommandText = """
                    DELETE FROM sync_leases
                    WHERE sync_run_id = $syncRunId
                      AND owner_id = $ownerId;
                    """;
                AddParameter(command, "$syncRunId", syncRunId);
                AddParameter(command, "$ownerId", ownerId);
                command.ExecuteNonQuery();
            }

            return true;
        });
    }

    public SyncRunSnapshot ReadActiveSyncRun()
    {
        EnsureInitialized();

        using var connection = OpenConnection();
        ReconcileExpiredSyncWork(connection, DateTimeOffset.UtcNow);
        return ReadActiveSyncRun(connection);
    }

    public int UpsertMessageMetadataBatch(
        int accountId,
        int folderId,
        IReadOnlyCollection<MessageMetadata> messages,
        string stateJson,
        uint? highestUid)
    {
        EnsureInitialized();

        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();

        var now = DateTimeOffset.UtcNow.ToString("O");

        foreach (var message in messages)
            UpsertMessageMetadata(connection, transaction, accountId, folderId, message, now);

        MarkFolderSyncSucceeded(connection, transaction, accountId, folderId, stateJson, highestUid, now);

        transaction.Commit();
        return messages.Count;
    }

    public void MarkFolderSyncSucceeded(
        int accountId,
        int folderId,
        string stateJson,
        uint? highestUid)
    {
        EnsureInitialized();

        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();

        MarkFolderSyncSucceeded(
            connection,
            transaction,
            accountId,
            folderId,
            stateJson,
            highestUid,
            DateTimeOffset.UtcNow.ToString("O"));

        transaction.Commit();
    }

    public void MarkFolderSyncFailed(
        int accountId,
        int folderId,
        string error)
    {
        EnsureInitialized();

        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();

        var now = DateTimeOffset.UtcNow.ToString("O");

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO sync_state (
                account_id,
                folder_id,
                last_error_at,
                last_error
            )
            VALUES (
                $accountId,
                $folderId,
                $lastErrorAt,
                $lastError
            )
            ON CONFLICT(account_id, folder_id) DO UPDATE SET
                last_error_at = excluded.last_error_at,
                last_error = excluded.last_error;
            """;
        AddParameter(command, "$accountId", accountId);
        AddParameter(command, "$folderId", folderId);
        AddParameter(command, "$lastErrorAt", now);
        AddParameter(command, "$lastError", error);
        command.ExecuteNonQuery();

        transaction.Commit();
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

        using (var command = connection.CreateCommand())
        {
            command.CommandText = "PRAGMA busy_timeout = 5000;";
            command.ExecuteNonQuery();
        }

        using (var command = connection.CreateCommand())
        {
            command.CommandText = "PRAGMA journal_mode = WAL;";
            command.ExecuteNonQuery();
        }

        using (var command = connection.CreateCommand())
        {
            command.CommandText = "PRAGMA foreign_keys = ON;";
            command.ExecuteNonQuery();
        }

        return connection;
    }

    private static int ExecuteScalarInt(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = command.ExecuteScalar();
        return Convert.ToInt32(value);
    }

    private static IReadOnlyList<string> ReadMessageFolders(SqliteConnection connection, int messageId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT DISTINCT f.path
            FROM message_locations ml
            JOIN folders f ON f.id = ml.folder_id
            WHERE ml.message_id = $messageId
              AND ml.deleted_locally = 0
              AND ml.expunged = 0
            ORDER BY f.path COLLATE NOCASE;
            """;
        AddParameter(command, "$messageId", messageId);

        using var reader = command.ExecuteReader();
        var folders = new List<string>();

        while (reader.Read())
            folders.Add(reader.GetString(0));

        return folders;
    }

    private static IReadOnlyList<MessageRecipient> ReadMessageRecipients(SqliteConnection connection, int messageId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT type, name, email
            FROM message_recipients
            WHERE message_id = $messageId
            ORDER BY type COLLATE NOCASE, id;
            """;
        AddParameter(command, "$messageId", messageId);

        using var reader = command.ExecuteReader();
        var recipients = new List<MessageRecipient>();

        while (reader.Read())
        {
            recipients.Add(new(
                Type: reader.GetString(reader.GetOrdinal("type")),
                Name: GetNullableString(reader, "name"),
                Email: GetNullableString(reader, "email")));
        }

        return recipients;
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
                    history_days = $historyDays,
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
                    history_days,
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
                    $historyDays,
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
        AddParameter(command, "$historyDays", account.HistoryDays);
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

    private static void UpsertMessageMetadata(
        SqliteConnection connection,
        SqliteTransaction transaction,
        int accountId,
        int folderId,
        MessageMetadata message,
        string now)
    {
        var messageId = FindExistingMessageId(connection, transaction, accountId, folderId, message);

        if (messageId is int existingMessageId)
        {
            UpdateMessage(connection, transaction, existingMessageId, message, now);
            UpsertMessageLocation(connection, transaction, existingMessageId, accountId, folderId, message, now);
            return;
        }

        var insertedMessageId = InsertMessage(connection, transaction, accountId, message, now);
        UpsertMessageLocation(connection, transaction, insertedMessageId, accountId, folderId, message, now);
    }

    private static int? FindExistingMessageId(
        SqliteConnection connection,
        SqliteTransaction transaction,
        int accountId,
        int folderId,
        MessageMetadata message)
    {
        var existingLocation = ExecuteScalarNullableInt(
            connection,
            transaction,
            """
            SELECT message_id
            FROM message_locations
            WHERE account_id = $accountId
              AND folder_id = $folderId
              AND provider_uid = $providerUid
            LIMIT 1;
            """,
            command =>
            {
                AddParameter(command, "$accountId", accountId);
                AddParameter(command, "$folderId", folderId);
                AddParameter(command, "$providerUid", message.ProviderUid);
            });

        if (existingLocation is not null)
            return existingLocation;

        if (!string.IsNullOrWhiteSpace(message.ProviderMessageKey))
        {
            var existingProviderMessage = ExecuteScalarNullableInt(
                connection,
                transaction,
                """
                SELECT id
                FROM messages
                WHERE account_id = $accountId
                  AND provider_message_key = $providerMessageKey
                ORDER BY id
                LIMIT 1;
                """,
                command =>
                {
                    AddParameter(command, "$accountId", accountId);
                    AddParameter(command, "$providerMessageKey", message.ProviderMessageKey);
                });

            if (existingProviderMessage is not null)
                return existingProviderMessage;
        }

        if (!string.IsNullOrWhiteSpace(message.MessageIdHeader))
        {
            return ExecuteScalarNullableInt(
                connection,
                transaction,
                """
                SELECT id
                FROM messages
                WHERE account_id = $accountId
                  AND message_id_header = $messageIdHeader
                ORDER BY id
                LIMIT 1;
                """,
                command =>
                {
                    AddParameter(command, "$accountId", accountId);
                    AddParameter(command, "$messageIdHeader", message.MessageIdHeader);
                });
        }

        return null;
    }

    private static int InsertMessage(
        SqliteConnection connection,
        SqliteTransaction transaction,
        int accountId,
        MessageMetadata message,
        string now)
    {
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO messages (
                    account_id,
                    provider_message_key,
                    provider_thread_key,
                    message_id_header,
                    in_reply_to,
                    references_header,
                    thread_key,
                    subject,
                    normalized_subject,
                    from_name,
                    from_email,
                    date_sent,
                    date_received,
                    has_attachments,
                    size_bytes,
                    raw_headers,
                    body_downloaded,
                    created_at,
                    updated_at
                )
                VALUES (
                    $accountId,
                    $providerMessageKey,
                    $providerThreadKey,
                    $messageIdHeader,
                    $inReplyTo,
                    $referencesHeader,
                    $threadKey,
                    $subject,
                    $normalizedSubject,
                    $fromName,
                    $fromEmail,
                    $dateSent,
                    $dateReceived,
                    $hasAttachments,
                    $sizeBytes,
                    $rawHeaders,
                    0,
                    $createdAt,
                    $updatedAt
                );
                """;
            AddMessageParameters(command, accountId, message);
            AddParameter(command, "$createdAt", now);
            AddParameter(command, "$updatedAt", now);
            command.ExecuteNonQuery();
        }

        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "SELECT last_insert_rowid();";
            return Convert.ToInt32(command.ExecuteScalar());
        }
    }

    private static void UpdateMessage(
        SqliteConnection connection,
        SqliteTransaction transaction,
        int messageId,
        MessageMetadata message,
        string now)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE messages
            SET
                provider_message_key = COALESCE($providerMessageKey, provider_message_key),
                provider_thread_key = COALESCE($providerThreadKey, provider_thread_key),
                message_id_header = COALESCE($messageIdHeader, message_id_header),
                in_reply_to = COALESCE($inReplyTo, in_reply_to),
                references_header = COALESCE($referencesHeader, references_header),
                thread_key = COALESCE($threadKey, thread_key),
                subject = COALESCE($subject, subject),
                normalized_subject = COALESCE($normalizedSubject, normalized_subject),
                from_name = COALESCE($fromName, from_name),
                from_email = COALESCE($fromEmail, from_email),
                date_sent = COALESCE($dateSent, date_sent),
                date_received = COALESCE($dateReceived, date_received),
                has_attachments = $hasAttachments,
                size_bytes = COALESCE($sizeBytes, size_bytes),
                raw_headers = COALESCE($rawHeaders, raw_headers),
                updated_at = $updatedAt
            WHERE id = $id;
            """;
        AddParameter(command, "$id", messageId);
        AddMessageParameters(command, accountId: 0, message);
        AddParameter(command, "$updatedAt", now);
        command.ExecuteNonQuery();
    }

    private static void AddMessageParameters(
        SqliteCommand command,
        int accountId,
        MessageMetadata message)
    {
        if (accountId != 0)
            AddParameter(command, "$accountId", accountId);

        AddParameter(command, "$providerMessageKey", BlankToNull(message.ProviderMessageKey));
        AddParameter(command, "$providerThreadKey", BlankToNull(message.ProviderThreadKey));
        AddParameter(command, "$messageIdHeader", BlankToNull(message.MessageIdHeader));
        AddParameter(command, "$inReplyTo", BlankToNull(message.InReplyTo));
        AddParameter(command, "$referencesHeader", BlankToNull(message.ReferencesHeader));
        AddParameter(command, "$threadKey", BlankToNull(message.ThreadKey));
        AddParameter(command, "$subject", BlankToNull(message.Subject));
        AddParameter(command, "$normalizedSubject", BlankToNull(message.NormalizedSubject));
        AddParameter(command, "$fromName", BlankToNull(message.FromName));
        AddParameter(command, "$fromEmail", BlankToNull(message.FromEmail));
        AddParameter(command, "$dateSent", BlankToNull(message.DateSent));
        AddParameter(command, "$dateReceived", BlankToNull(message.DateReceived));
        AddParameter(command, "$hasAttachments", message.HasAttachments ? 1 : 0);
        AddParameter(command, "$sizeBytes", message.SizeBytes);
        AddParameter(command, "$rawHeaders", BlankToNull(message.RawHeaders));
    }

    private static void UpsertMessageLocation(
        SqliteConnection connection,
        SqliteTransaction transaction,
        int messageId,
        int accountId,
        int folderId,
        MessageMetadata message,
        string now)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO message_locations (
                message_id,
                account_id,
                folder_id,
                provider_uid,
                provider_message_key,
                flags,
                labels,
                deleted_locally,
                expunged,
                first_seen_at,
                last_seen_at
            )
            VALUES (
                $messageId,
                $accountId,
                $folderId,
                $providerUid,
                $providerMessageKey,
                $flags,
                $labels,
                0,
                0,
                $firstSeenAt,
                $lastSeenAt
            )
            ON CONFLICT(account_id, folder_id, provider_uid) DO UPDATE SET
                message_id = excluded.message_id,
                provider_message_key = COALESCE(excluded.provider_message_key, message_locations.provider_message_key),
                flags = excluded.flags,
                labels = excluded.labels,
                deleted_locally = 0,
                expunged = 0,
                last_seen_at = excluded.last_seen_at;
            """;
        AddParameter(command, "$messageId", messageId);
        AddParameter(command, "$accountId", accountId);
        AddParameter(command, "$folderId", folderId);
        AddParameter(command, "$providerUid", message.ProviderUid);
        AddParameter(command, "$providerMessageKey", BlankToNull(message.ProviderMessageKey));
        AddParameter(command, "$flags", BlankToNull(message.Flags));
        AddParameter(command, "$labels", BlankToNull(message.Labels));
        AddParameter(command, "$firstSeenAt", now);
        AddParameter(command, "$lastSeenAt", now);
        command.ExecuteNonQuery();
    }

    private static void UpsertMessageBody(
        SqliteConnection connection,
        SqliteTransaction transaction,
        MessageBodyContent body,
        string now)
    {
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO message_bodies (
                    message_id,
                    plain_text,
                    html_text,
                    normalized_text,
                    detected_language,
                    normalized_at
                )
                VALUES (
                    $messageId,
                    $plainText,
                    $htmlText,
                    $normalizedText,
                    NULL,
                    $normalizedAt
                )
                ON CONFLICT(message_id) DO UPDATE SET
                    plain_text = excluded.plain_text,
                    html_text = excluded.html_text,
                    normalized_text = excluded.normalized_text,
                    detected_language = excluded.detected_language,
                    normalized_at = excluded.normalized_at;
                """;
            AddParameter(command, "$messageId", body.MessageId);
            AddParameter(command, "$plainText", BlankToNull(body.PlainText));
            AddParameter(command, "$htmlText", BlankToNull(body.HtmlText));
            AddParameter(command, "$normalizedText", BlankToNull(body.NormalizedText));
            AddParameter(command, "$normalizedAt", now);
            command.ExecuteNonQuery();
        }

        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = "DELETE FROM message_recipients WHERE message_id = $messageId;";
            AddParameter(command, "$messageId", body.MessageId);
            command.ExecuteNonQuery();
        }

        foreach (var recipient in body.Recipients)
            InsertMessageRecipient(connection, transaction, body.MessageId, recipient);

        RefreshMessageSearchDocument(connection, transaction, body.MessageId);

        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE messages
                SET
                    body_downloaded = 1,
                    updated_at = $updatedAt
                WHERE id = $messageId;
                """;
            AddParameter(command, "$messageId", body.MessageId);
            AddParameter(command, "$updatedAt", now);
            command.ExecuteNonQuery();
        }
    }

    private static void InsertMessageRecipient(
        SqliteConnection connection,
        SqliteTransaction transaction,
        int messageId,
        MessageRecipient recipient)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO message_recipients (
                message_id,
                type,
                name,
                email
            )
            VALUES (
                $messageId,
                $type,
                $name,
                $email
            );
            """;
        AddParameter(command, "$messageId", messageId);
        AddParameter(command, "$type", recipient.Type);
        AddParameter(command, "$name", BlankToNull(recipient.Name));
        AddParameter(command, "$email", BlankToNull(recipient.Email));
        command.ExecuteNonQuery();
    }

    private static void RefreshMessageSearchDocument(
        SqliteConnection connection,
        SqliteTransaction transaction,
        int messageId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO message_search_docs (
                message_id,
                subject,
                from_email,
                from_name,
                recipients,
                body
            )
            VALUES (
                $messageId,
                (SELECT subject FROM messages WHERE id = $messageId),
                (SELECT from_email FROM messages WHERE id = $messageId),
                (SELECT from_name FROM messages WHERE id = $messageId),
                (
                    SELECT group_concat(recipient_text, ' ')
                    FROM (
                        SELECT TRIM(COALESCE(name, '') || ' ' || COALESCE(email, '')) AS recipient_text
                        FROM message_recipients
                        WHERE message_id = $messageId
                        ORDER BY type, id
                    )
                ),
                (SELECT normalized_text FROM message_bodies WHERE message_id = $messageId)
            )
            ON CONFLICT(message_id) DO UPDATE SET
                subject = excluded.subject,
                from_email = excluded.from_email,
                from_name = excluded.from_name,
                recipients = excluded.recipients,
                body = excluded.body;
            """;
        AddParameter(command, "$messageId", messageId);
        command.ExecuteNonQuery();
    }

    private static void MarkFolderSyncSucceeded(
        SqliteConnection connection,
        SqliteTransaction transaction,
        int accountId,
        int folderId,
        string stateJson,
        uint? highestUid,
        string now)
    {
        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO sync_state (
                    account_id,
                    folder_id,
                    state_json,
                    last_success_at,
                    last_error_at,
                    last_error
                )
                VALUES (
                    $accountId,
                    $folderId,
                    $stateJson,
                    $lastSuccessAt,
                    NULL,
                    NULL
                )
                ON CONFLICT(account_id, folder_id) DO UPDATE SET
                    state_json = excluded.state_json,
                    last_success_at = excluded.last_success_at,
                    last_error_at = NULL,
                    last_error = NULL;
                """;
            AddParameter(command, "$accountId", accountId);
            AddParameter(command, "$folderId", folderId);
            AddParameter(command, "$stateJson", BlankToNull(stateJson));
            AddParameter(command, "$lastSuccessAt", now);
            command.ExecuteNonQuery();
        }

        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE folders
                SET
                    last_uid = CASE
                        WHEN $lastUid IS NULL THEN last_uid
                        WHEN last_uid IS NULL THEN $lastUid
                        WHEN $lastUid > last_uid THEN $lastUid
                        ELSE last_uid
                    END,
                    last_sync_at = $lastSyncAt
                WHERE id = $folderId;
                """;
            AddParameter(command, "$folderId", folderId);
            AddParameter(command, "$lastUid", highestUid);
            AddParameter(command, "$lastSyncAt", now);
            command.ExecuteNonQuery();
        }

        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE accounts
                SET last_sync_at = $lastSyncAt
                WHERE id = $accountId;
                """;
            AddParameter(command, "$accountId", accountId);
            AddParameter(command, "$lastSyncAt", now);
            command.ExecuteNonQuery();
        }
    }

    private static int? ExecuteScalarNullableInt(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        Action<SqliteCommand> bind)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        bind(command);

        var value = command.ExecuteScalar();
        return value is null || value is DBNull ? null : Convert.ToInt32(value);
    }

    private static IReadOnlyList<int> ResolveAccountIds(
        SqliteConnection connection,
        IReadOnlyList<string> accountFilters)
    {
        if (!HasValues(accountFilters))
            return [];

        var accountIds = new HashSet<int>();

        foreach (var filter in accountFilters.Where(value => !string.IsNullOrWhiteSpace(value)))
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT id
                FROM accounts
                WHERE name COLLATE NOCASE = $accountFilter
                   OR email_address COLLATE NOCASE = $accountFilter
                   OR CAST(id AS TEXT) = $accountFilter;
                """;
            AddParameter(command, "$accountFilter", filter.Trim());

            using var reader = command.ExecuteReader();
            while (reader.Read())
                accountIds.Add(reader.GetInt32(0));
        }

        return accountIds.Order().ToList();
    }

    private static int ReadScopedAccountCount(
        SqliteConnection connection,
        IReadOnlyList<int> accountIds,
        IReadOnlyList<string> accountFilters)
    {
        if (accountIds.Count > 0)
            return accountIds.Count;

        if (HasValues(accountFilters))
            return 0;

        return ExecuteScalarInt(connection, "SELECT COUNT(*) FROM accounts WHERE enabled = 1;");
    }

    private static IReadOnlyList<MessageSearchFolderState> ReadMessageSearchFolderStates(
        SqliteConnection connection,
        IReadOnlyList<int> accountIds,
        IReadOnlyList<string> folderRoles)
    {
        var query = CreateDbQuery(connection, SearchReadinessFoldersSql);
        ApplyScopeConditions(query, accountIds, null, folderRoles, null);

        using var command = query.CreateCommand();

        using var reader = (SqliteDataReader)command.ExecuteReader();
        var folders = new List<MessageSearchFolderState>();

        while (reader.Read())
        {
            folders.Add(new(
                FolderId: reader.GetInt32(reader.GetOrdinal("folder_id")),
                HistoryDays: reader.GetInt32(reader.GetOrdinal("history_days")),
                StateJson: GetNullableString(reader, "state_json"),
                LastSuccessAt: GetNullableString(reader, "last_success_at"),
                LastErrorAt: GetNullableString(reader, "last_error_at")));
        }

        return folders;
    }

    private static MessageSearchReadinessCounts ReadMessageSearchCounts(
        SqliteConnection connection,
        IReadOnlyList<int> accountIds,
        MessageSearchReadinessRequest request)
    {
        var query = CreateDbQuery(connection, SearchReadinessCountsSql);
        ApplyScopeConditions(query, accountIds, request.FromEmail, request.FolderRoles, request.HasAttachment);

        using var command = query.CreateCommand();

        using var reader = (SqliteDataReader)command.ExecuteReader();
        if (!reader.Read())
            return new(0, 0, 0, 0, 0);

        return new(
            MetadataMessages: reader.GetInt32(reader.GetOrdinal("metadata_messages")),
            IndexedMessageBodies: reader.GetInt32(reader.GetOrdinal("indexed_message_bodies")),
            MessageSearchDocs: reader.GetInt32(reader.GetOrdinal("message_search_docs")),
            FtsRows: reader.GetInt32(reader.GetOrdinal("fts_rows")),
            PendingMessageBodies: reader.GetInt32(reader.GetOrdinal("pending_message_bodies")));
    }

    private static void ApplyScopeConditions(
        DbQuery query,
        IReadOnlyList<int> accountIds,
        string fromEmail,
        IReadOnlyList<string> folderRoles,
        bool? hasAttachment)
    {
        if (accountIds.Count > 0)
            query.SetCondition("accountIds", accountIds);

        query.SetCondition("fromEmail", BlankToNull(fromEmail), StringOperator.Is, ignoreIfNull: true);

        if (HasValues(folderRoles))
            query.SetCondition("folderRoles", folderRoles.Select(role => role.Trim().ToLowerInvariant()).ToList());

        if (hasAttachment is bool hasAttachments)
            query.SetCondition("hasAttachments", hasAttachments ? 1 : 0);
    }

    private static DbQuery CreateDbQuery(SqliteConnection connection, string sql)
    {
        var query = new DbQuery(connection, sql)
        {
            ParserHints = ParserHints.UseCustomSyntaxForParams
        };

        query.FormatParameterName += (_, e) =>
        {
            var parameterName = $"${e.FormattedName}";
            e.FormattedName = parameterName;
            e.FormattedForSqlPlaceholder = parameterName;
        };

        return query;
    }

    private static bool IsFolderMetadataComplete(MessageSearchFolderState folder)
    {
        if (string.IsNullOrWhiteSpace(folder.LastSuccessAt))
            return false;

        if (!string.IsNullOrWhiteSpace(folder.LastErrorAt))
            return false;

        var state = ParseMetadataSyncState(folder.StateJson);
        if (state is null)
            return false;

        var coversHistory = state.SinceDays == 0 || state.SinceDays <= folder.HistoryDays;
        var uncapped = state.MaxPerFolder == 0 || state.SelectedCount >= state.MatchedCount;
        var fetchedSelected = state.FetchedCount >= state.SelectedCount;

        return coversHistory
            && uncapped
            && fetchedSelected
            && state.MissingCount == 0;
    }

    private static MetadataSyncState ParseMetadataSyncState(string stateJson)
    {
        if (string.IsNullOrWhiteSpace(stateJson))
            return null;

        try
        {
            using var document = JsonDocument.Parse(stateJson);
            var root = document.RootElement;

            if (!TryGetJsonInt(root, "since_days", out var sinceDays)
                || !TryGetJsonInt(root, "max_per_folder", out var maxPerFolder)
                || !TryGetJsonInt(root, "matched_count", out var matchedCount)
                || !TryGetJsonInt(root, "selected_count", out var selectedCount)
                || !TryGetJsonInt(root, "fetched_count", out var fetchedCount)
                || !TryGetJsonInt(root, "missing_count", out var missingCount))
                return null;

            return new(sinceDays, maxPerFolder, matchedCount, selectedCount, fetchedCount, missingCount);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static bool TryGetJsonInt(JsonElement root, string name, out int value)
    {
        value = 0;
        return root.TryGetProperty(name, out var property)
            && property.ValueKind == JsonValueKind.Number
            && property.TryGetInt32(out value);
    }

    private static SyncRunSnapshot ReadActiveSyncRun(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                id,
                scope_key,
                account_name,
                folder_filter,
                status,
                phase,
                done,
                total,
                started_at,
                last_progress_at,
                completed_at,
                last_error
            FROM sync_runs
            WHERE status IN ('queued', 'running')
            ORDER BY
                CASE status WHEN 'running' THEN 0 ELSE 1 END,
                COALESCE(requested_at, started_at),
                id
            LIMIT 1;
            """;

        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadSyncRunSnapshot(reader) : null;
    }

    private static SyncRunSnapshot ReadSyncRunSnapshot(SqliteConnection connection, string syncRunId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                id,
                scope_key,
                account_name,
                folder_filter,
                status,
                phase,
                done,
                total,
                started_at,
                last_progress_at,
                completed_at,
                last_error
            FROM sync_runs
            WHERE id = $id
            LIMIT 1;
            """;
        AddParameter(command, "$id", syncRunId);

        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadSyncRunSnapshot(reader) : null;
    }

    private static void ReconcileExpiredSyncWork(SqliteConnection connection, DateTimeOffset now)
    {
        var nowText = now.ToString("O");

        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                UPDATE sync_runs
                SET
                    status = 'failed',
                    last_progress_at = $lastProgressAt,
                    completed_at = $completedAt,
                    last_error = $lastError
                WHERE status = 'running'
                  AND id IN (
                      SELECT sync_run_id
                      FROM sync_leases
                      WHERE lease_expires_at < $now
                  );
                """;
            AddParameter(command, "$lastProgressAt", nowText);
            AddParameter(command, "$completedAt", nowText);
            AddParameter(
                command,
                "$lastError",
                $"Sync lease expired after no heartbeat for {SyncLeaseDuration.TotalMinutes:0.#} minutes; marking failed.");
            AddParameter(command, "$now", nowText);
            command.ExecuteNonQuery();
        }

        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                DELETE FROM sync_leases
                WHERE lease_expires_at < $now;
                """;
            AddParameter(command, "$now", nowText);
            command.ExecuteNonQuery();
        }

        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                UPDATE sync_runs
                SET
                    status = 'failed',
                    last_progress_at = $lastProgressAt,
                    completed_at = $completedAt,
                    last_error = $lastError
                WHERE status = 'queued'
                  AND owner_id IS NOT NULL
                  AND last_progress_at < $staleBefore;
                """;
            AddParameter(command, "$lastProgressAt", nowText);
            AddParameter(command, "$completedAt", nowText);
            AddParameter(
                command,
                "$lastError",
                $"Queued sync run became stale after no heartbeat for {QueuedSyncRunAfter.TotalMinutes:0.#} minutes; marking failed.");
            AddParameter(command, "$staleBefore", now.Subtract(QueuedSyncRunAfter).ToString("O"));
            command.ExecuteNonQuery();
        }
    }

    private static bool HasActiveSyncLease(SqliteConnection connection, string scopeKey, DateTimeOffset now)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT 1
            FROM sync_leases
            WHERE scope_key = $scopeKey
              AND lease_expires_at >= $now
            LIMIT 1;
            """;
        AddParameter(command, "$scopeKey", scopeKey);
        AddParameter(command, "$now", now.ToString("O"));
        return command.ExecuteScalar() is not null;
    }

    private static bool HasMatchingSyncLease(
        SqliteConnection connection,
        string scopeKey,
        string syncRunId,
        string ownerId,
        DateTimeOffset now)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT 1
            FROM sync_leases
            WHERE scope_key = $scopeKey
              AND sync_run_id = $syncRunId
              AND owner_id = $ownerId
              AND lease_expires_at >= $now
            LIMIT 1;
            """;
        AddParameter(command, "$scopeKey", scopeKey);
        AddParameter(command, "$syncRunId", syncRunId);
        AddParameter(command, "$ownerId", ownerId);
        AddParameter(command, "$now", now.ToString("O"));
        return command.ExecuteScalar() is not null;
    }

    private static bool HasQueuedSyncRun(SqliteConnection connection, string scopeKey)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT 1
            FROM sync_runs
            WHERE scope_key = $scopeKey
              AND status = 'queued'
            LIMIT 1;
            """;
        AddParameter(command, "$scopeKey", scopeKey);
        return command.ExecuteScalar() is not null;
    }

    private static bool IsOldestQueuedSyncRun(SqliteConnection connection, string scopeKey, string syncRunId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id
            FROM sync_runs
            WHERE scope_key = $scopeKey
              AND status = 'queued'
            ORDER BY COALESCE(requested_at, started_at), id
            LIMIT 1;
            """;
        AddParameter(command, "$scopeKey", scopeKey);
        var oldest = command.ExecuteScalar() as string;
        return string.Equals(oldest, syncRunId, StringComparison.Ordinal);
    }

    private static bool IsQueuedSyncRunOwner(SqliteConnection connection, string syncRunId, string ownerId)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT 1
            FROM sync_runs
            WHERE id = $syncRunId
              AND owner_id = $ownerId
              AND status = 'queued'
            LIMIT 1;
            """;
        AddParameter(command, "$syncRunId", syncRunId);
        AddParameter(command, "$ownerId", ownerId);
        return command.ExecuteScalar() is not null;
    }

    private static string InsertSyncRun(
        SqliteConnection connection,
        int? accountId,
        string accountName,
        string folderFilter,
        string phase,
        int total,
        string ownerId,
        string scopeKey,
        string status,
        DateTimeOffset now)
    {
        var syncRunId = BuildSyncRunId();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO sync_runs (
                id,
                scope_key,
                account_id,
                account_name,
                folder_filter,
                owner_id,
                status,
                phase,
                done,
                total,
                requested_at,
                started_at,
                last_progress_at
            )
            VALUES (
                $id,
                $scopeKey,
                $accountId,
                $accountName,
                $folderFilter,
                $ownerId,
                $status,
                $phase,
                0,
                $total,
                $requestedAt,
                $startedAt,
                $lastProgressAt
            );
            """;
        AddParameter(command, "$id", syncRunId);
        AddParameter(command, "$scopeKey", scopeKey);
        AddParameter(command, "$accountId", accountId);
        AddParameter(command, "$accountName", BlankToNull(accountName) ?? "(unknown)");
        AddParameter(command, "$folderFilter", BlankToNull(folderFilter));
        AddParameter(command, "$ownerId", ownerId);
        AddParameter(command, "$status", status);
        AddParameter(command, "$phase", phase);
        AddParameter(command, "$total", Math.Max(0, total));
        AddParameter(command, "$requestedAt", now.ToString("O"));
        AddParameter(command, "$startedAt", now.ToString("O"));
        AddParameter(command, "$lastProgressAt", now.ToString("O"));
        command.ExecuteNonQuery();
        return syncRunId;
    }

    private static void MarkSyncRunRunning(
        SqliteConnection connection,
        string syncRunId,
        string ownerId,
        DateTimeOffset now)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE sync_runs
            SET
                status = 'running',
                owner_id = $ownerId,
                started_at = $startedAt,
                last_progress_at = $lastProgressAt,
                completed_at = NULL,
                last_error = NULL
            WHERE id = $id
              AND status = 'queued';
            """;
        AddParameter(command, "$id", syncRunId);
        AddParameter(command, "$ownerId", ownerId);
        AddParameter(command, "$startedAt", now.ToString("O"));
        AddParameter(command, "$lastProgressAt", now.ToString("O"));
        command.ExecuteNonQuery();
    }

    private static void InsertSyncLease(
        SqliteConnection connection,
        string scopeKey,
        string syncRunId,
        string ownerId,
        DateTimeOffset now)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO sync_leases (
                scope_key,
                sync_run_id,
                owner_id,
                heartbeat_at,
                lease_expires_at
            )
            VALUES (
                $scopeKey,
                $syncRunId,
                $ownerId,
                $heartbeatAt,
                $leaseExpiresAt
            )
            ON CONFLICT(scope_key) DO UPDATE SET
                sync_run_id = excluded.sync_run_id,
                owner_id = excluded.owner_id,
                heartbeat_at = excluded.heartbeat_at,
                lease_expires_at = excluded.lease_expires_at;
            """;
        AddParameter(command, "$scopeKey", scopeKey);
        AddParameter(command, "$syncRunId", syncRunId);
        AddParameter(command, "$ownerId", ownerId);
        AddParameter(command, "$heartbeatAt", now.ToString("O"));
        AddParameter(command, "$leaseExpiresAt", now.Add(SyncLeaseDuration).ToString("O"));
        command.ExecuteNonQuery();
    }

    private static void HeartbeatQueuedSyncRun(
        SqliteConnection connection,
        string syncRunId,
        string ownerId,
        DateTimeOffset now)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE sync_runs
            SET last_progress_at = $lastProgressAt
            WHERE id = $id
              AND owner_id = $ownerId
              AND status = 'queued';
            """;
        AddParameter(command, "$id", syncRunId);
        AddParameter(command, "$ownerId", ownerId);
        AddParameter(command, "$lastProgressAt", now.ToString("O"));
        command.ExecuteNonQuery();
    }

    private static readonly TimeSpan SyncLeaseDuration = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan QueuedSyncRunAfter = TimeSpan.FromMinutes(5);

    private static SyncRunSnapshot ReadSyncRunSnapshot(SqliteDataReader reader)
    {
        var done = reader.GetInt32(reader.GetOrdinal("done"));
        var total = reader.GetInt32(reader.GetOrdinal("total"));
        var status = reader.GetString(reader.GetOrdinal("status"));
        var startedAt = reader.GetString(reader.GetOrdinal("started_at"));
        var completedAt = GetNullableString(reader, "completed_at");
        var elapsedSeconds = CalculateElapsedSeconds(startedAt, completedAt);
        var percent = CalculatePercent(status, done, total);
        var estimatedRemainingSeconds = CalculateEstimatedRemainingSeconds(done, total, elapsedSeconds);

        return new(
            Id: reader.GetString(reader.GetOrdinal("id")),
            ScopeKey: reader.GetString(reader.GetOrdinal("scope_key")),
            AccountName: reader.GetString(reader.GetOrdinal("account_name")),
            FolderFilter: GetNullableString(reader, "folder_filter"),
            Status: status,
            Phase: reader.GetString(reader.GetOrdinal("phase")),
            Done: done,
            Total: total,
            Percent: percent,
            ElapsedSeconds: elapsedSeconds,
            EstimatedRemainingSeconds: estimatedRemainingSeconds,
            EstimateConfidence: EstimateConfidence(done, total, elapsedSeconds),
            StartedAt: startedAt,
            LastProgressAt: reader.GetString(reader.GetOrdinal("last_progress_at")),
            CompletedAt: completedAt,
            LastError: GetNullableString(reader, "last_error"));
    }

    private static int CalculateElapsedSeconds(string startedAt, string completedAt)
    {
        if (!DateTimeOffset.TryParse(startedAt, out var start))
            return 0;

        var end = DateTimeOffset.TryParse(completedAt, out var completed)
            ? completed
            : DateTimeOffset.UtcNow;

        return Math.Max(0, Convert.ToInt32(Math.Round((end - start).TotalSeconds)));
    }

    private static int CalculatePercent(string status, int done, int total)
    {
        if (total <= 0)
            return status.Equals("succeeded", StringComparison.OrdinalIgnoreCase) ? 100 : 0;

        return Math.Clamp(Convert.ToInt32(Math.Round(done * 100d / total)), 0, 100);
    }

    private static int? CalculateEstimatedRemainingSeconds(int done, int total, int elapsedSeconds)
    {
        if (done <= 0 || total <= 0 || done >= total || elapsedSeconds <= 0)
            return null;

        return Convert.ToInt32(Math.Round(elapsedSeconds * (total - done) / (double)done));
    }

    private static string EstimateConfidence(int done, int total, int elapsedSeconds)
    {
        if (done <= 0 || total <= 0 || elapsedSeconds < 10)
            return "low";

        if (done < Math.Min(10, total) || done < total / 2)
            return "medium";

        return "high";
    }

    private static string BuildSyncRunId()
    {
        var timestamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmssfff");
        var suffix = Guid.NewGuid().ToString("N")[..8];
        return $"sync-{timestamp}-{suffix}";
    }

    private static string BuildSyncOwnerId()
    {
        var timestamp = DateTimeOffset.UtcNow.ToString("yyyyMMdd-HHmmssfff");
        var suffix = Guid.NewGuid().ToString("N")[..8];
        return $"owner-{Environment.ProcessId}-{timestamp}-{suffix}";
    }

    private static T RunInImmediateTransaction<T>(SqliteConnection connection, Func<T> action)
    {
        ExecuteNonQuery(connection, transaction: null, "BEGIN IMMEDIATE;");

        try
        {
            var result = action();
            ExecuteNonQuery(connection, transaction: null, "COMMIT;");
            return result;
        }
        catch
        {
            ExecuteNonQuery(connection, transaction: null, "ROLLBACK;");
            throw;
        }
    }

    private static bool HasValues(IReadOnlyList<string> values) =>
        values is not null && values.Any(value => !string.IsNullOrWhiteSpace(value));

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

    private sealed record MessageSearchFolderState(
        int FolderId,
        int HistoryDays,
        string StateJson,
        string LastSuccessAt,
        string LastErrorAt);

    private sealed record MessageSearchReadinessCounts(
        int MetadataMessages,
        int IndexedMessageBodies,
        int MessageSearchDocs,
        int FtsRows,
        int PendingMessageBodies);

    private sealed record MetadataSyncState(
        int SinceDays,
        int MaxPerFolder,
        int MatchedCount,
        int SelectedCount,
        int FetchedCount,
        int MissingCount);

    private const string MigrationTableSql = """
        CREATE TABLE IF NOT EXISTS schema_migrations (
            version INTEGER PRIMARY KEY,
            name TEXT NOT NULL,
            applied_at TEXT NOT NULL
        );
        """;

    private const string SearchMessagesSql = """
        SELECT
            m.id AS message_id,
            a.name AS account_name,
            (
                SELECT group_concat(folder_path, ', ')
                FROM (
                    SELECT DISTINCT f.path AS folder_path
                    FROM message_locations ml
                    JOIN folders f ON f.id = ml.folder_id
                    WHERE ml.message_id = m.id
                      AND ml.deleted_locally = 0
                      AND ml.expunged = 0
                    ORDER BY f.path COLLATE NOCASE
                )
            ) AS folders,
            COALESCE(m.date_sent, m.date_received) AS message_date,
            m.from_name,
            m.from_email,
            m.subject,
            m.has_attachments,
            snippet(messages_fts, -1, '[', ']', ' ... ', $snippetTokens) AS snippet,
            bm25(messages_fts) AS score
        FROM messages_fts
        JOIN message_search_docs d ON d.message_id = messages_fts.rowid
        JOIN messages m ON m.id = d.message_id
        JOIN accounts a ON a.id = m.account_id
        WHERE messages_fts MATCH $fts
        {AND
            {m.account_id [accountIds]}
            {m.from_email COLLATE NOCASE [fromEmail]}
            {m.has_attachments [hasAttachments]}
            {EXISTS (
                SELECT 1
                FROM message_locations ml_filter
                JOIN folders f_filter ON f_filter.id = ml_filter.folder_id
                WHERE ml_filter.message_id = m.id
                  AND ml_filter.deleted_locally = 0
                  AND ml_filter.expunged = 0
                  {AND {f_filter.role [folderRoles]}}
            )}}
        ORDER BY score, COALESCE(m.date_sent, m.date_received, '') DESC
        LIMIT $limit;
        """;

    private const string SearchReadinessFoldersSql = """
        SELECT
            f.id AS folder_id,
            a.history_days,
            s.state_json,
            s.last_success_at,
            s.last_error_at
        FROM folders f
        JOIN accounts a ON a.id = f.account_id
        LEFT JOIN sync_state s ON s.account_id = f.account_id
            AND s.folder_id = f.id
        WHERE a.enabled = 1
          AND f.selectable = 1
          AND f.sync_enabled = 1
        {AND
            {a.id [accountIds]}
            {f.role [folderRoles]}}
        ORDER BY a.name COLLATE NOCASE, f.path COLLATE NOCASE;
        """;

    private const string SearchReadinessCountsSql = """
        SELECT
            COUNT(DISTINCT m.id) AS metadata_messages,
            COUNT(DISTINCT CASE
                WHEN m.body_downloaded = 1 AND b.message_id IS NOT NULL THEN m.id
            END) AS indexed_message_bodies,
            COUNT(DISTINCT d.message_id) AS message_search_docs,
            COUNT(DISTINCT fts.rowid) AS fts_rows,
            COUNT(DISTINCT CASE
                WHEN m.body_downloaded = 0 OR b.message_id IS NULL THEN m.id
            END) AS pending_message_bodies
        FROM messages m
        JOIN accounts a ON a.id = m.account_id
        JOIN message_locations ml ON ml.message_id = m.id
        JOIN folders f ON f.id = ml.folder_id
        LEFT JOIN message_bodies b ON b.message_id = m.id
        LEFT JOIN message_search_docs d ON d.message_id = m.id
        LEFT JOIN messages_fts fts ON fts.rowid = m.id
        WHERE a.enabled = 1
          AND f.selectable = 1
          AND f.sync_enabled = 1
          AND ml.deleted_locally = 0
          AND ml.expunged = 0
        {AND
            {m.account_id [accountIds]}
            {m.from_email COLLATE NOCASE [fromEmail]}
            {m.has_attachments [hasAttachments]}
            {f.role [folderRoles]}}
        """;
}
