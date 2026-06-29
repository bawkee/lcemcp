using Microsoft.Data.Sqlite;
using SqlBinder;
using SqlBinder.Parsing;

namespace LceMcp;

internal sealed partial class EmailDatabase
{
    private const int MaxExceptionDetailsChars = 16_000;
    private static readonly TimeSpan AttachmentLeaseDuration = TimeSpan.FromMinutes(5);

    // Global means all configured accounts and messages. Open means unresolved
    // failure issues, not currently open files or attachment access handles.
    private static IReadOnlyDictionary<string, int> ReadGlobalOpenAttachmentFailureCounts(
        SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT error_code, COUNT(*)
            FROM attachment_extraction_failures
            WHERE status = 'open'
            GROUP BY error_code
            ORDER BY error_code;
            """;
        return ReadFailureCounts(command);
    }

    // The scoped counterpart used by search readiness: count unresolved issues only
    // for attachments reachable through the request's account/message/folder filters.
    private static IReadOnlyDictionary<string, int> ReadOpenAttachmentFailureCounts(
        SqliteConnection connection,
        IReadOnlyList<int> accountIds,
        MessageSearchReadinessRequest request)
    {
        var query = CreateDbQuery(connection, ScopedAttachmentFailureCountsSql);
        ApplyScopeConditions(
            query,
            accountIds,
            request.FromEmail,
            request.ToEmail,
            request.FolderRoles,
            request.HasAttachment,
            request.DateFrom,
            request.DateTo);
        ApplyAttachmentConditions(query, request.MimeTypes, request.FilenameContains);

        using var command = query.CreateCommand();
        query.AddSqlParameter("$dateFrom", request.DateFrom);
        query.AddSqlParameter("$dateTo", request.DateTo);
        return ReadFailureCounts((SqliteCommand)command);
    }

    private static IReadOnlyDictionary<string, int> ReadFailureCounts(SqliteCommand command)
    {
        using var reader = command.ExecuteReader();
        var values = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        while (reader.Read())
            values[reader.GetString(0)] = reader.GetInt32(1);
        return values;
    }

    public IReadOnlyList<AttachmentExtractionFailure> ListAttachmentExtractionFailures(
        AttachmentExtractionFailureQuery request)
    {
        EnsureInitialized();
        ValidateFailureQuery(request);

        using var connection = OpenConnection();
        var accountIds = ResolveAccountIds(connection, request.AccountFilters);
        if (HasValues(request.AccountFilters) && accountIds.Count == 0)
            return [];

        var query = CreateDbQuery(connection, AttachmentFailuresSql);
        if (request.AttachmentIds is { Count: > 0 })
            query.SetCondition("attachmentIds", request.AttachmentIds.Distinct().ToList());
        if (accountIds.Count > 0)
            query.SetCondition("accountIds", accountIds);
        if (request.ErrorCodes is { Count: > 0 })
            query.SetCondition("errorCodes", request.ErrorCodes.Select(NormalizeErrorCode).ToList());

        query.SetCondition("status", BlankToNull(request.Status), StringOperator.Is, ignoreIfNull: true);

        using var command = query.CreateCommand();
        query.AddSqlParameter("$limit", request.Limit);
        using var reader = (SqliteDataReader)command.ExecuteReader();
        var failures = new List<AttachmentExtractionFailure>();

        while (reader.Read())
        {
            failures.Add(new(
                FailureId: reader.GetInt32(reader.GetOrdinal("failure_id")),
                AttachmentId: reader.GetInt32(reader.GetOrdinal("attachment_id")),
                MessageId: reader.GetInt32(reader.GetOrdinal("message_id")),
                AccountName: reader.GetString(reader.GetOrdinal("account_name")),
                DisplayPath: reader.GetString(reader.GetOrdinal("display_path")),
                MimeType: GetNullableString(reader, "mime_type"),
                Stage: reader.GetString(reader.GetOrdinal("stage")),
                ErrorCode: reader.GetString(reader.GetOrdinal("error_code")),
                ErrorSummary: GetNullableString(reader, "error_summary"),
                ExceptionType: GetNullableString(reader, "exception_type"),
                Extractor: GetNullableString(reader, "extractor"),
                ExtractorVersion: GetNullableString(reader, "extractor_version"),
                OccurrenceCount: reader.GetInt32(reader.GetOrdinal("occurrence_count")),
                FirstSeenAt: reader.GetString(reader.GetOrdinal("first_seen_at")),
                LastCheckedAt: reader.GetString(reader.GetOrdinal("last_checked_at")),
                ResolvedAt: GetNullableString(reader, "resolved_at"),
                Status: reader.GetString(reader.GetOrdinal("status"))));
        }

        return failures;
    }

    public IReadOnlyList<int> ReadDueAttachmentExtractionIds(
        string accountFilter,
        int limit,
        DateTimeOffset? now = null)
    {
        EnsureInitialized();
        using var connection = OpenConnection();
        var accountIds = ResolveAccountIds(
            connection,
            string.IsNullOrWhiteSpace(accountFilter) ? [] : [accountFilter]);
        if (!string.IsNullOrWhiteSpace(accountFilter) && accountIds.Count == 0)
            return [];

        var query = CreateDbQuery(connection, DueAttachmentExtractionsSql);
        if (accountIds.Count > 0)
            query.SetCondition("accountIds", accountIds);

        using var command = query.CreateCommand();
        query.AddSqlParameter("$now", (now ?? DateTimeOffset.UtcNow).ToString("O"));
        query.AddSqlParameter("$limit", Math.Clamp(limit, 1, 100));
        using var reader = (SqliteDataReader)command.ExecuteReader();
        var ids = new List<int>();
        while (reader.Read())
            ids.Add(reader.GetInt32(0));
        return ids;
    }

    public int QueuePdfOcrUpgradeCandidates(string accountFilter = null)
    {
        EnsureInitialized();
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE attachments
            SET
                extraction_status = 'pending',
                extraction_next_attempt_at = NULL,
                extraction_completed_at = NULL,
                extraction_error_code = NULL,
                extraction_error = NULL,
                extractor_version = CASE
                    WHEN extraction_error_code = 'ocr_disabled' THEN 'ocr-disabled'
                    ELSE extractor_version
                END,
                updated_at = $updatedAt
            WHERE storage_key IS NOT NULL
              AND is_container = 0
              AND (
                  (
                      extraction_status IN ('done', 'empty')
                      AND COALESCE(extractor_version, '') <> $processorVersion
                  )
                  OR (
                      extraction_status = 'failed'
                      AND extraction_error_code = 'ocr_disabled'
                  )
              )
              AND (
                  sniffed_mime_type = 'application/pdf'
                  OR lower(filename) LIKE '%.pdf'
              )
              AND (
                  $accountFilter IS NULL
                  OR message_id IN (
                      SELECT m.id
                      FROM messages m
                      JOIN accounts acc ON acc.id = m.account_id
                      WHERE acc.name = $accountFilter COLLATE NOCASE
                         OR acc.email_address = $accountFilter COLLATE NOCASE
                  )
              );
            """;
        AddParameter(command, "$processorVersion", AttachmentProcessor.ProcessorVersion);
        AddParameter(command, "$accountFilter", string.IsNullOrWhiteSpace(accountFilter) ? null : accountFilter.Trim());
        AddParameter(command, "$updatedAt", DateTimeOffset.UtcNow.ToString("O"));
        return command.ExecuteNonQuery();
    }

    public bool IsPdfOcrUpgradeCandidate(int attachmentId)
    {
        EnsureInitialized();
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT EXISTS (
                SELECT 1
                FROM attachments
                WHERE id = $attachmentId
                  AND extraction_status IN ('pending', 'retry_wait')
                  AND COALESCE(extractor_version, '') <> $processorVersion
                  AND (
                      sniffed_mime_type = 'application/pdf'
                      OR lower(filename) LIKE '%.pdf'
                  )
            );
            """;
        AddParameter(command, "$attachmentId", attachmentId);
        AddParameter(command, "$processorVersion", AttachmentProcessor.ProcessorVersion);
        return Convert.ToInt32(command.ExecuteScalar()) == 1;
    }

    public AttachmentExtractionClaim ClaimAttachmentExtraction(
        int attachmentId,
        string triggerKind,
        string clientName = null,
        DateTimeOffset? now = null)
    {
        EnsureInitialized();
        var claimTime = now ?? DateTimeOffset.UtcNow;

        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        var attachment = ReadAttachmentForClaim(connection, transaction, attachmentId);
        if (attachment is null || string.IsNullOrWhiteSpace(attachment.StorageKey))
            return null;

        var explicitRetry = triggerKind == "explicit_retry";
        if (!CanClaimAttachment(connection, transaction, attachmentId, explicitRetry, claimTime))
            return null;

        var leaseToken = Guid.NewGuid().ToString("N");
        var startedAt = claimTime.ToString("O");
        using var attempt = connection.CreateCommand();
        attempt.Transaction = transaction;
        attempt.CommandText = """
            INSERT INTO attachment_extraction_attempts (
                attachment_id,
                stage,
                trigger_kind,
                client_name,
                extractor,
                extractor_version,
                started_at,
                outcome
            )
            VALUES (
                $attachmentId,
                'extraction',
                $triggerKind,
                $clientName,
                $extractor,
                $extractorVersion,
                $startedAt,
                'running'
            );
            SELECT last_insert_rowid();
            """;
        AddParameter(attempt, "$attachmentId", attachmentId);
        AddParameter(attempt, "$triggerKind", triggerKind);
        AddParameter(attempt, "$clientName", BlankToNull(clientName));
        AddParameter(attempt, "$extractor", attachment.Extractor);
        AddParameter(attempt, "$extractorVersion", attachment.ExtractorVersion);
        AddParameter(attempt, "$startedAt", startedAt);
        var attemptId = Convert.ToInt64(attempt.ExecuteScalar());

        using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = """
            UPDATE attachments
            SET
                extraction_status = 'running',
                extraction_attempts = (
                    SELECT COUNT(*)
                    FROM attachment_extraction_attempts
                    WHERE attachment_id = $attachmentId
                      AND stage = 'extraction'
                ),
                extraction_started_at = $startedAt,
                extraction_lease_until = $leaseUntil,
                extraction_lease_token = $leaseToken,
                extraction_next_attempt_at = NULL,
                updated_at = $startedAt
            WHERE id = $attachmentId
              AND extraction_status <> 'running';
            """;
        AddParameter(update, "$attachmentId", attachmentId);
        AddParameter(update, "$startedAt", startedAt);
        AddParameter(update, "$leaseUntil", claimTime.Add(AttachmentLeaseDuration).ToString("O"));
        AddParameter(update, "$leaseToken", leaseToken);
        if (update.ExecuteNonQuery() != 1)
            return null;

        transaction.Commit();
        return new(attemptId, leaseToken, triggerKind, attachment);
    }

    public bool CompleteAttachmentExtraction(
        AttachmentExtractionClaim claim,
        AttachmentContent result,
        DateTimeOffset? now = null)
    {
        EnsureInitialized();
        var completionTime = now ?? DateTimeOffset.UtcNow;
        var completedAt = completionTime.ToString("O");

        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        if (!OwnsAttachmentClaim(connection, transaction, claim, completionTime))
            return false;

        using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                UPDATE attachments
                SET
                    sniffed_mime_type = $sniffedMimeType,
                    size_bytes = $sizeBytes,
                    compressed_size_bytes = $compressedSizeBytes,
                    uncompressed_size_bytes = $uncompressedSizeBytes,
                    content_hash = COALESCE($contentHash, content_hash),
                    storage_key = COALESCE($storageKey, storage_key),
                    is_container = $isContainer,
                    extraction_status = $extractionStatus,
                    extraction_error_code = $extractionErrorCode,
                    extraction_error = $extractionError,
                    extractor = $extractor,
                    extractor_version = $extractorVersion,
                    extracted_text_available = $extractedTextAvailable,
                    ocr_text_available = $ocrTextAvailable,
                    extraction_lease_until = NULL,
                    extraction_lease_token = NULL,
                    updated_at = $updatedAt
                WHERE id = $attachmentId
                  AND extraction_lease_token = $leaseToken;
                """;
            AddParameter(command, "$attachmentId", claim.Attachment.AttachmentId);
            AddParameter(command, "$leaseToken", claim.LeaseToken);
            AddParameter(command, "$sniffedMimeType", BlankToNull(result.SniffedMimeType));
            AddParameter(command, "$sizeBytes", result.SizeBytes);
            AddParameter(command, "$compressedSizeBytes", result.CompressedSizeBytes);
            AddParameter(command, "$uncompressedSizeBytes", result.UncompressedSizeBytes);
            AddParameter(command, "$contentHash", BlankToNull(result.ContentHash));
            AddParameter(command, "$storageKey", BlankToNull(result.StorageKey));
            AddParameter(command, "$isContainer", result.IsContainer ? 1 : 0);
            AddParameter(command, "$extractionStatus", BlankToNull(result.ExtractionStatus) ?? "failed");
            AddParameter(command, "$extractionErrorCode", BlankToNull(result.ExtractionErrorCode));
            AddParameter(command, "$extractionError", BlankToNull(result.ExtractionError));
            AddParameter(command, "$extractor", BlankToNull(result.Extractor));
            AddParameter(command, "$extractorVersion", BlankToNull(result.ExtractorVersion));
            AddParameter(command, "$extractedTextAvailable", string.IsNullOrWhiteSpace(result.ExtractedText) ? 0 : 1);
            AddParameter(command, "$ocrTextAvailable", string.IsNullOrWhiteSpace(result.OcrText) ? 0 : 1);
            AddParameter(command, "$updatedAt", completedAt);
            if (command.ExecuteNonQuery() != 1)
                return false;
        }

        UpsertAttachmentText(connection, transaction, claim.Attachment.AttachmentId, result, completedAt);
        RecordAttachmentProcessingOutcome(
            connection,
            transaction,
            claim.Attachment.AttachmentId,
            result,
            claim.TriggerKind,
            clientName: null,
            completedAt,
            claim.AttemptId);
        RefreshAttachmentSearchDocument(connection, transaction, claim.Attachment.AttachmentId);

        var retainedIds = new HashSet<int> { claim.Attachment.AttachmentId };
        var rootId = claim.Attachment.RootAttachmentId ?? claim.Attachment.AttachmentId;
        foreach (var child in result.Children ?? [])
            UpsertAttachmentTree(
                connection,
                transaction,
                claim.Attachment.MessageId,
                child,
                claim.Attachment.AttachmentId,
                rootId,
                completedAt,
                claim.TriggerKind,
                retainedIds);

        if (result.ExtractionStatus is "done" or "empty")
        {
            DeleteStaleAttachmentDescendants(
                connection,
                transaction,
                claim.Attachment.AttachmentId,
                retainedIds);
        }
        transaction.Commit();
        return true;
    }

    public int RecoverExpiredAttachmentExtractions(DateTimeOffset? now = null)
    {
        _paths.EnsureDataDirectories();
        ApplyMigrations();
        return RecoverExpiredAttachmentExtractionsCore(now ?? DateTimeOffset.UtcNow);
    }

    private int RecoverExpiredAttachmentExtractionsCore(DateTimeOffset recoveryTime)
    {
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();

        using var select = connection.CreateCommand();
        select.Transaction = transaction;
        select.CommandText = """
            SELECT
                a.id,
                (
                    SELECT attempt.id
                    FROM attachment_extraction_attempts attempt
                    WHERE attempt.attachment_id = a.id
                      AND attempt.outcome = 'running'
                    ORDER BY attempt.id DESC
                    LIMIT 1
                ) AS attempt_id
            FROM attachments a
            WHERE a.extraction_status = 'running'
              AND a.extraction_lease_until IS NOT NULL
              AND a.extraction_lease_until <= $now;
            """;
        AddParameter(select, "$now", recoveryTime.ToString("O"));
        var expired = new List<(int AttachmentId, long AttemptId)>();
        using (var reader = select.ExecuteReader())
        {
            while (reader.Read())
            {
                if (!reader.IsDBNull(1))
                    expired.Add((reader.GetInt32(0), reader.GetInt64(1)));
            }
        }

        foreach (var item in expired)
        {
            // Recovery completes the abandoned attempt through the ordinary failure
            // lifecycle so attempt budgets, issue deduplication, and backoff stay aligned.
            var failure = AttachmentFailureContent(
                ReadAttachmentForClaim(connection, transaction, item.AttachmentId),
                "worker_crashed",
                "The previous attachment extraction worker lease expired.");
            RecordAttachmentProcessingOutcome(
                connection,
                transaction,
                item.AttachmentId,
                failure,
                "automatic_retry",
                clientName: null,
                recoveryTime.ToString("O"),
                item.AttemptId);
        }

        transaction.Commit();
        return expired.Count;
    }

    internal void UpsertAttachmentMetadata(int messageId, AttachmentContent attachment)
    {
        // Persist the MIME/body-structure row before requesting bytes. A rejected or
        // interrupted part download therefore remains visible and diagnosable.
        EnsureInitialized();
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        var now = DateTimeOffset.UtcNow.ToString("O");
        UpsertAttachmentMetadataOnly(connection, transaction, messageId, attachment, now);
        transaction.Commit();
    }

    private static void UpsertAttachmentMetadataOnly(
        SqliteConnection connection,
        SqliteTransaction transaction,
        int messageId,
        AttachmentContent attachment,
        string now)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO attachments (
                message_id,
                source_kind,
                part_id,
                filename,
                display_path,
                mime_type,
                sniffed_mime_type,
                size_bytes,
                is_container,
                download_status,
                extraction_status,
                created_at,
                updated_at
            )
            VALUES (
                $messageId,
                'email_part',
                $partId,
                $filename,
                $displayPath,
                $mimeType,
                $sniffedMimeType,
                $sizeBytes,
                $isContainer,
                'pending',
                'not_ready',
                $now,
                $now
            )
            ON CONFLICT(message_id, source_kind, display_path) DO UPDATE SET
                part_id = excluded.part_id,
                filename = excluded.filename,
                mime_type = excluded.mime_type,
                size_bytes = excluded.size_bytes,
                download_status = CASE
                    WHEN attachments.download_status = 'stored' THEN attachments.download_status
                    ELSE 'pending'
                END,
                updated_at = excluded.updated_at;
            """;
        AddParameter(command, "$messageId", messageId);
        AddParameter(command, "$partId", BlankToNull(attachment.PartId));
        AddParameter(command, "$filename", BlankToNull(attachment.Filename));
        AddParameter(command, "$displayPath", BlankToNull(attachment.DisplayPath) ?? "attachment");
        AddParameter(command, "$mimeType", BlankToNull(attachment.MimeType));
        AddParameter(command, "$sniffedMimeType", BlankToNull(attachment.SniffedMimeType));
        AddParameter(command, "$sizeBytes", attachment.SizeBytes);
        AddParameter(command, "$isContainer", attachment.IsContainer ? 1 : 0);
        AddParameter(command, "$now", now);
        command.ExecuteNonQuery();
    }

    internal void RecordBodySyncFailure(
        int messageId,
        string errorCode,
        string error,
        Exception exception)
    {
        // Body retries cover provider/MIME download work. Local extraction retries use
        // attachment claims instead and must not re-download an already stored object.
        EnsureInitialized();
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        using var read = connection.CreateCommand();
        read.Transaction = transaction;
        read.CommandText = "SELECT body_attempts FROM messages WHERE id = $messageId;";
        AddParameter(read, "$messageId", messageId);
        var attempts = Convert.ToInt32(read.ExecuteScalar()) + 1;
        var exhausted = attempts >= AttachmentFailureClassifier.AutomaticAttemptBudget(errorCode);
        var delay = TimeSpan.FromSeconds(30 * Math.Pow(4, Math.Max(0, attempts - 1)));
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        var now = DateTimeOffset.UtcNow;
        command.CommandText = """
            UPDATE messages
            SET
                body_attempts = $attempts,
                body_retry_exhausted = $exhausted,
                body_next_attempt_at = $nextAttemptAt,
                body_last_error_code = $errorCode,
                body_last_error = $error,
                updated_at = $updatedAt
            WHERE id = $messageId;
            """;
        AddParameter(command, "$messageId", messageId);
        AddParameter(command, "$attempts", attempts);
        AddParameter(command, "$exhausted", exhausted ? 1 : 0);
        AddParameter(command, "$nextAttemptAt", exhausted ? null : now.Add(delay).ToString("O"));
        AddParameter(command, "$errorCode", errorCode);
        AddParameter(command, "$error", BlankToNull(error));
        AddParameter(command, "$updatedAt", now.ToString("O"));
        command.ExecuteNonQuery();
        transaction.Commit();

        if (exception is not null)
            new AttachmentDiagnosticLogger(_paths).Write(0, "message_download", "automatic_retry", errorCode, exception.ToString());
    }

    public int RequeueExhaustedBodyRetries(
        string accountFilter,
        string folderFilter,
        int limit = 1000)
    {
        EnsureInitialized();
        var folders = ReadSyncFolders(accountFilter, folderFilter);
        if (folders.Count == 0)
            return 0;

        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        var folderParameters = folders.Select((_, index) => $"$folder{index}").ToList();
        command.CommandText = $"""
            UPDATE messages
            SET
                body_attempts = 0,
                body_retry_exhausted = 0,
                body_next_attempt_at = NULL,
                updated_at = $updatedAt
            WHERE id IN (
                SELECT DISTINCT m.id
                FROM messages m
                JOIN message_locations ml ON ml.message_id = m.id
                WHERE ml.folder_id IN ({string.Join(", ", folderParameters)})
                  AND m.body_retry_exhausted = 1
                  AND ml.deleted_locally = 0
                  AND ml.expunged = 0
                ORDER BY m.updated_at, m.id
                LIMIT $limit
            );
            """;
        for (var index = 0; index < folders.Count; index++)
            AddParameter(command, folderParameters[index], folders[index].Id);
        AddParameter(command, "$limit", Math.Clamp(limit, 1, 1000));
        AddParameter(command, "$updatedAt", DateTimeOffset.UtcNow.ToString("O"));
        return command.ExecuteNonQuery();
    }

    private static bool CanClaimAttachment(
        SqliteConnection connection,
        SqliteTransaction transaction,
        int attachmentId,
        bool explicitRetry,
        DateTimeOffset now)
    {
        // Explicit retries grant one attempt for an existing open extraction issue;
        // automatic workers may claim only due scheduled states.
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = explicitRetry
            ? """
                SELECT EXISTS (
                    SELECT 1
                    FROM attachments a
                    JOIN attachment_extraction_failures f ON f.attachment_id = a.id
                    WHERE a.id = $attachmentId
                      AND a.extraction_status <> 'running'
                      AND f.stage = 'extraction'
                      AND f.status = 'open'
                );
                """
            : """
                SELECT EXISTS (
                    SELECT 1
                    FROM attachments a
                    WHERE a.id = $attachmentId
                      AND a.extraction_status IN ('pending', 'retry_wait')
                      AND (a.extraction_next_attempt_at IS NULL OR a.extraction_next_attempt_at <= $now)
                );
                """;
        AddParameter(command, "$attachmentId", attachmentId);
        if (!explicitRetry)
            AddParameter(command, "$now", now.ToString("O"));
        return Convert.ToInt32(command.ExecuteScalar()) == 1;
    }

    private static bool OwnsAttachmentClaim(
        SqliteConnection connection,
        SqliteTransaction transaction,
        AttachmentExtractionClaim claim,
        DateTimeOffset now)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT EXISTS (
                SELECT 1
                FROM attachments
                WHERE id = $attachmentId
                  AND extraction_status = 'running'
                  AND extraction_lease_token = $leaseToken
                  AND extraction_lease_until > $now
            );
            """;
        AddParameter(command, "$attachmentId", claim.Attachment.AttachmentId);
        AddParameter(command, "$leaseToken", claim.LeaseToken);
        AddParameter(command, "$now", now.ToString("O"));
        return Convert.ToInt32(command.ExecuteScalar()) == 1;
    }

    private static StoredAttachment ReadAttachmentForClaim(
        SqliteConnection connection,
        SqliteTransaction transaction,
        int attachmentId)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            {StoredAttachmentSelectSql}
            WHERE a.id = $attachmentId
            LIMIT 1;
            """;
        AddParameter(command, "$attachmentId", attachmentId);
        using var reader = command.ExecuteReader();
        return reader.Read() ? ReadStoredAttachment(reader) : null;
    }

    private void RecordAttachmentProcessingOutcome(
        SqliteConnection connection,
        SqliteTransaction transaction,
        int attachmentId,
        AttachmentContent attachment,
        string triggerKind,
        string clientName,
        string now,
        long? attemptId)
    {
        // Keep three representations consistent in one transaction:
        // attempt history, deduplicated open issues, and the attachment's latest state.
        if (attachment.ExtractionStatus is not ("done" or "empty" or "failed")
            && string.IsNullOrWhiteSpace(attachment.ExtractionErrorCode)
            && string.IsNullOrWhiteSpace(attachment.DownloadErrorCode))
        {
            using var pending = connection.CreateCommand();
            pending.Transaction = transaction;
            pending.CommandText = """
                UPDATE attachments
                SET
                    extraction_status = $status,
                    extraction_attempts = (
                        SELECT COUNT(*)
                        FROM attachment_extraction_attempts
                        WHERE attachment_id = $attachmentId
                          AND stage = 'extraction'
                    ),
                    extraction_error_code = NULL,
                    extraction_error = NULL,
                    extractor = $extractor,
                    extractor_version = $extractorVersion,
                    updated_at = $updatedAt
                WHERE id = $attachmentId;
                """;
            AddParameter(pending, "$attachmentId", attachmentId);
            AddParameter(pending, "$status", attachment.ExtractionStatus);
            AddParameter(pending, "$extractor", BlankToNull(attachment.Extractor));
            AddParameter(pending, "$extractorVersion", BlankToNull(attachment.ExtractorVersion));
            AddParameter(pending, "$updatedAt", now);
            pending.ExecuteNonQuery();
            return;
        }

        var success = attachment.ExtractionStatus is "done" or "empty"
            && string.IsNullOrWhiteSpace(attachment.ExtractionErrorCode);
        var stage = !string.IsNullOrWhiteSpace(attachment.DownloadErrorCode)
            && string.IsNullOrWhiteSpace(attachment.StorageKey)
            ? "download"
            : "extraction";
        var errorCode = BlankToNull(attachment.ExtractionErrorCode)
            ?? BlankToNull(attachment.DownloadErrorCode);
        var outcome = success ? attachment.ExtractionStatus : "failed";
        var downloadSucceeded = triggerKind == "initial"
            && attachment.DownloadStatus == "stored"
            && !string.IsNullOrWhiteSpace(attachment.StorageKey);

        if (attemptId is null)
        {
            using var insert = connection.CreateCommand();
            insert.Transaction = transaction;
            insert.CommandText = """
                INSERT INTO attachment_extraction_attempts (
                    attachment_id,
                    stage,
                    trigger_kind,
                    client_name,
                    extractor,
                    extractor_version,
                    started_at,
                    completed_at,
                    outcome,
                    error_code,
                    exception_type,
                    exception_message,
                    exception_details
                )
                VALUES (
                    $attachmentId,
                    $stage,
                    $triggerKind,
                    $clientName,
                    $extractor,
                    $extractorVersion,
                    $startedAt,
                    $completedAt,
                    $outcome,
                    $errorCode,
                    $exceptionType,
                    $exceptionMessage,
                    $exceptionDetails
                );
                SELECT last_insert_rowid();
                """;
            AddAttemptParameters(insert, attachmentId, stage, triggerKind, clientName, attachment, now, outcome, errorCode);
            attemptId = Convert.ToInt64(insert.ExecuteScalar());
        }
        else
        {
            using var updateAttempt = connection.CreateCommand();
            updateAttempt.Transaction = transaction;
            updateAttempt.CommandText = """
                UPDATE attachment_extraction_attempts
                SET
                    extractor = $extractor,
                    extractor_version = $extractorVersion,
                    completed_at = $completedAt,
                    outcome = $outcome,
                    error_code = $errorCode,
                    exception_type = $exceptionType,
                    exception_message = $exceptionMessage,
                    exception_details = $exceptionDetails
                WHERE id = $attemptId
                  AND attachment_id = $attachmentId
                  AND outcome = 'running';
                """;
            AddParameter(updateAttempt, "$attemptId", attemptId);
            AddParameter(updateAttempt, "$attachmentId", attachmentId);
            AddParameter(updateAttempt, "$extractor", BlankToNull(attachment.Extractor));
            AddParameter(updateAttempt, "$extractorVersion", BlankToNull(attachment.ExtractorVersion));
            AddParameter(updateAttempt, "$completedAt", now);
            AddParameter(updateAttempt, "$outcome", outcome);
            AddParameter(updateAttempt, "$errorCode", errorCode);
            AddParameter(updateAttempt, "$exceptionType", BlankToNull(attachment.ExceptionType));
            AddParameter(updateAttempt, "$exceptionMessage", Truncate(attachment.ExceptionMessage, 2_000));
            AddParameter(updateAttempt, "$exceptionDetails", Truncate(attachment.ExceptionDetails, MaxExceptionDetailsChars));
            updateAttempt.ExecuteNonQuery();
        }

        if (success)
        {
            ResolveAttachmentFailures(connection, transaction, attachmentId, "extraction", attemptId.Value, now);
            if (downloadSucceeded)
                ResolveAttachmentFailures(connection, transaction, attachmentId, "download", attemptId.Value, now);
            UpdateAttachmentProjection(connection, transaction, attachmentId, attachment, "completed", now, null, downloadSucceeded);
        }
        else
        {
            if (downloadSucceeded)
                ResolveAttachmentFailures(connection, transaction, attachmentId, "download", attemptId.Value, now);

            errorCode ??= "unknown_extractor_failure";
            SupersedeChangedFailures(connection, transaction, attachmentId, stage, errorCode, now);
            UpsertAttachmentFailure(
                connection,
                transaction,
                attachmentId,
                stage,
                errorCode,
                BlankToNull(attachment.ExtractionError) ?? BlankToNull(attachment.DownloadError),
                attemptId.Value,
                now);
            var state = DetermineFailureState(connection, transaction, attachmentId, stage, errorCode, triggerKind, now);
            if (stage == "download")
                UpdateDownloadProjection(connection, transaction, attachmentId, attachment, state.Status, now, state.NextAttemptAt);
            else
                UpdateAttachmentProjection(connection, transaction, attachmentId, attachment, state.Status, now, state.NextAttemptAt, downloadSucceeded);

            if (!string.IsNullOrWhiteSpace(attachment.ExceptionDetails))
                new AttachmentDiagnosticLogger(_paths).Write(attachmentId, stage, triggerKind, errorCode, attachment.ExceptionDetails);
        }
    }

    private static void AddAttemptParameters(
        SqliteCommand command,
        int attachmentId,
        string stage,
        string triggerKind,
        string clientName,
        AttachmentContent attachment,
        string now,
        string outcome,
        string errorCode)
    {
        AddParameter(command, "$attachmentId", attachmentId);
        AddParameter(command, "$stage", stage);
        AddParameter(command, "$triggerKind", triggerKind);
        AddParameter(command, "$clientName", BlankToNull(clientName));
        AddParameter(command, "$extractor", BlankToNull(attachment.Extractor));
        AddParameter(command, "$extractorVersion", BlankToNull(attachment.ExtractorVersion));
        AddParameter(command, "$startedAt", now);
        AddParameter(command, "$completedAt", now);
        AddParameter(command, "$outcome", outcome);
        AddParameter(command, "$errorCode", errorCode);
        AddParameter(command, "$exceptionType", BlankToNull(attachment.ExceptionType));
        AddParameter(command, "$exceptionMessage", Truncate(attachment.ExceptionMessage, 2_000));
        AddParameter(command, "$exceptionDetails", Truncate(attachment.ExceptionDetails, MaxExceptionDetailsChars));
    }

    private static void ResolveAttachmentFailures(
        SqliteConnection connection,
        SqliteTransaction transaction,
        int attachmentId,
        string stage,
        long attemptId,
        string now)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE attachment_extraction_failures
            SET
                status = 'resolved',
                resolved_at = $resolvedAt,
                resolved_by_attempt_id = $attemptId,
                last_checked_at = $resolvedAt
            WHERE attachment_id = $attachmentId
              AND stage = $stage
              AND status = 'open';
            """;
        AddParameter(command, "$attachmentId", attachmentId);
        AddParameter(command, "$stage", stage);
        AddParameter(command, "$attemptId", attemptId);
        AddParameter(command, "$resolvedAt", now);
        command.ExecuteNonQuery();
    }

    private static void SupersedeChangedFailures(
        SqliteConnection connection,
        SqliteTransaction transaction,
        int attachmentId,
        string stage,
        string errorCode,
        string now)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE attachment_extraction_failures
            SET
                status = 'superseded',
                resolved_at = $resolvedAt,
                last_checked_at = $resolvedAt
            WHERE attachment_id = $attachmentId
              AND stage = $stage
              AND error_code <> $errorCode
              AND status = 'open';
            """;
        AddParameter(command, "$attachmentId", attachmentId);
        AddParameter(command, "$stage", stage);
        AddParameter(command, "$errorCode", errorCode);
        AddParameter(command, "$resolvedAt", now);
        command.ExecuteNonQuery();
    }

    private static void UpsertAttachmentFailure(
        SqliteConnection connection,
        SqliteTransaction transaction,
        int attachmentId,
        string stage,
        string errorCode,
        string errorSummary,
        long attemptId,
        string now)
    {
        using var update = connection.CreateCommand();
        update.Transaction = transaction;
        update.CommandText = """
            UPDATE attachment_extraction_failures
            SET
                latest_attempt_id = $attemptId,
                occurrence_count = occurrence_count + 1,
                last_checked_at = $now,
                error_summary = $errorSummary
            WHERE attachment_id = $attachmentId
              AND stage = $stage
              AND error_code = $errorCode
              AND status = 'open';
            """;
        AddParameter(update, "$attachmentId", attachmentId);
        AddParameter(update, "$stage", stage);
        AddParameter(update, "$errorCode", errorCode);
        AddParameter(update, "$attemptId", attemptId);
        AddParameter(update, "$now", now);
        AddParameter(update, "$errorSummary", errorSummary);
        if (update.ExecuteNonQuery() == 1)
            return;

        using var insert = connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = """
            INSERT INTO attachment_extraction_failures (
                attachment_id,
                stage,
                error_code,
                error_summary,
                status,
                first_attempt_id,
                latest_attempt_id,
                occurrence_count,
                first_seen_at,
                last_checked_at
            )
            VALUES (
                $attachmentId,
                $stage,
                $errorCode,
                $errorSummary,
                'open',
                $attemptId,
                $attemptId,
                1,
                $now,
                $now
            );
            """;
        AddParameter(insert, "$attachmentId", attachmentId);
        AddParameter(insert, "$stage", stage);
        AddParameter(insert, "$errorCode", errorCode);
        AddParameter(insert, "$errorSummary", errorSummary);
        AddParameter(insert, "$attemptId", attemptId);
        AddParameter(insert, "$now", now);
        insert.ExecuteNonQuery();
    }

    private static AttachmentFailureState DetermineFailureState(
        SqliteConnection connection,
        SqliteTransaction transaction,
        int attachmentId,
        string stage,
        string errorCode,
        string triggerKind,
        string now)
    {
        if (triggerKind is "explicit_retry" or "extractor_upgrade")
            return new("failed", null);

        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT COUNT(*)
            FROM attachment_extraction_attempts
            WHERE attachment_id = $attachmentId
              AND stage = $stage
              AND trigger_kind IN ('initial', 'automatic_retry', 'legacy_migration');
            """;
        AddParameter(command, "$attachmentId", attachmentId);
        AddParameter(command, "$stage", stage);
        var automaticAttempts = Convert.ToInt32(command.ExecuteScalar());
        if (automaticAttempts >= AttachmentFailureClassifier.AutomaticAttemptBudget(errorCode))
            return new("failed", null);

        var delay = TimeSpan.FromSeconds(30 * Math.Pow(4, Math.Max(0, automaticAttempts - 1)));
        return new("retry_wait", DateTimeOffset.Parse(now).Add(delay).ToString("O"));
    }

    private static void UpdateAttachmentProjection(
        SqliteConnection connection,
        SqliteTransaction transaction,
        int attachmentId,
        AttachmentContent attachment,
        string state,
        string now,
        string nextAttemptAt,
        bool downloadSucceeded)
    {
        var success = state == "completed";
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE attachments
            SET
                extraction_status = $status,
                extraction_attempts = (
                    SELECT COUNT(*)
                    FROM attachment_extraction_attempts
                    WHERE attachment_id = $attachmentId
                      AND stage = 'extraction'
                ),
                extraction_next_attempt_at = $nextAttemptAt,
                extraction_completed_at = $completedAt,
                extraction_error_code = $errorCode,
                extraction_error = $error,
                extractor = $extractor,
                extractor_version = $extractorVersion,
                download_status = CASE WHEN $downloadSucceeded = 1 THEN $downloadStatus ELSE download_status END,
                download_next_attempt_at = CASE WHEN $downloadSucceeded = 1 THEN NULL ELSE download_next_attempt_at END,
                download_completed_at = CASE WHEN $downloadSucceeded = 1 THEN $updatedAt ELSE download_completed_at END,
                download_error_code = CASE WHEN $downloadSucceeded = 1 THEN NULL ELSE download_error_code END,
                download_error = CASE WHEN $downloadSucceeded = 1 THEN NULL ELSE download_error END,
                extraction_lease_until = NULL,
                extraction_lease_token = NULL,
                updated_at = $updatedAt
            WHERE id = $attachmentId;
            """;
        AddParameter(command, "$attachmentId", attachmentId);
        AddParameter(command, "$status", success ? attachment.ExtractionStatus : state);
        AddParameter(command, "$nextAttemptAt", nextAttemptAt);
        AddParameter(command, "$completedAt", success || state == "failed" ? now : null);
        AddParameter(command, "$errorCode", success ? null : BlankToNull(attachment.ExtractionErrorCode) ?? BlankToNull(attachment.DownloadErrorCode) ?? "unknown_extractor_failure");
        AddParameter(command, "$error", success ? null : BlankToNull(attachment.ExtractionError));
        AddParameter(command, "$extractor", BlankToNull(attachment.Extractor));
        AddParameter(command, "$extractorVersion", BlankToNull(attachment.ExtractorVersion));
        AddParameter(command, "$downloadSucceeded", downloadSucceeded ? 1 : 0);
        AddParameter(command, "$downloadStatus", BlankToNull(attachment.DownloadStatus) ?? "stored");
        AddParameter(command, "$updatedAt", now);
        command.ExecuteNonQuery();
    }

    private static void UpdateDownloadProjection(
        SqliteConnection connection,
        SqliteTransaction transaction,
        int attachmentId,
        AttachmentContent attachment,
        string state,
        string now,
        string nextAttemptAt)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE attachments
            SET
                download_status = CASE
                    WHEN storage_key IS NOT NULL THEN 'stored'
                    WHEN $state = 'retry_wait' THEN 'retry_wait'
                    ELSE $resultStatus
                END,
                download_next_attempt_at = $nextAttemptAt,
                download_completed_at = CASE WHEN $state = 'failed' THEN $now ELSE NULL END,
                download_error_code = $errorCode,
                download_error = $error,
                extraction_status = CASE
                    WHEN storage_key IS NULL THEN $state
                    ELSE extraction_status
                END,
                extraction_attempts = (
                    SELECT COUNT(*)
                    FROM attachment_extraction_attempts
                    WHERE attachment_id = $attachmentId
                      AND stage = 'extraction'
                ),
                extraction_next_attempt_at = CASE
                    WHEN storage_key IS NULL THEN $nextAttemptAt
                    ELSE extraction_next_attempt_at
                END,
                extraction_completed_at = CASE
                    WHEN storage_key IS NULL AND $state = 'failed' THEN $now
                    ELSE extraction_completed_at
                END,
                extraction_error_code = CASE
                    WHEN storage_key IS NULL THEN $errorCode
                    ELSE extraction_error_code
                END,
                extraction_error = CASE
                    WHEN storage_key IS NULL THEN $error
                    ELSE extraction_error
                END,
                extraction_lease_until = NULL,
                extraction_lease_token = NULL,
                updated_at = $now
            WHERE id = $attachmentId;
            """;
        AddParameter(command, "$attachmentId", attachmentId);
        AddParameter(command, "$state", state);
        AddParameter(command, "$resultStatus", BlankToNull(attachment.DownloadStatus) ?? "failed");
        AddParameter(command, "$nextAttemptAt", nextAttemptAt);
        AddParameter(command, "$now", now);
        AddParameter(command, "$errorCode", BlankToNull(attachment.DownloadErrorCode) ?? BlankToNull(attachment.ExtractionErrorCode) ?? "unknown_extractor_failure");
        AddParameter(command, "$error", BlankToNull(attachment.DownloadError) ?? BlankToNull(attachment.ExtractionError));
        command.ExecuteNonQuery();
    }

    private static void DeleteStaleAttachmentDescendants(
        SqliteConnection connection,
        SqliteTransaction transaction,
        int attachmentId,
        IReadOnlySet<int> retainedIds)
    {
        // A successful container rescan is authoritative. Remove archive entries no
        // longer produced, while failed rescans deliberately retain the previous tree.
        var placeholders = retainedIds.Select((_, index) => $"$retained{index}").ToList();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = $"""
            WITH RECURSIVE descendants(id) AS (
                SELECT id
                FROM attachments
                WHERE parent_attachment_id = $attachmentId

                UNION ALL

                SELECT child.id
                FROM attachments child
                JOIN descendants parent ON child.parent_attachment_id = parent.id
            )
            DELETE FROM attachments
            WHERE id IN (SELECT id FROM descendants)
              AND id NOT IN ({string.Join(", ", placeholders)});
            """;
        AddParameter(command, "$attachmentId", attachmentId);
        var index = 0;
        foreach (var retainedId in retainedIds)
            AddParameter(command, $"$retained{index++}", retainedId);
        command.ExecuteNonQuery();
    }

    private static AttachmentContent AttachmentFailureContent(
        StoredAttachment attachment,
        string errorCode,
        string error) =>
        new(
            attachment.SourceKind,
            attachment.PartId,
            attachment.Filename,
            attachment.DisplayPath,
            attachment.ArchiveEntryPath,
            attachment.MimeType,
            attachment.SniffedMimeType,
            attachment.SizeBytes,
            attachment.CompressedSizeBytes,
            attachment.UncompressedSizeBytes,
            attachment.ContentHash,
            attachment.StorageKey,
            attachment.IsContainer,
            attachment.NestingDepth,
            attachment.DownloadStatus,
            attachment.DownloadError,
            "failed",
            error,
            null,
            null,
            attachment.Extractor,
            [],
            errorCode,
            attachment.ExtractorVersion);

    private static void ValidateFailureQuery(AttachmentExtractionFailureQuery request)
    {
        if (request is null)
            throw new ArgumentNullException(nameof(request));
        if (request.Limit is < 1 or > 100)
            throw new CliException("Attachment failure limit must be between 1 and 100.", 2);
        if (request.Status is not null
            && request.Status is not "open"
            && request.Status is not "resolved"
            && request.Status is not "superseded")
            throw new CliException("Attachment failure status must be open, resolved, or superseded.", 2);
    }

    private static string NormalizeErrorCode(string value) =>
        value?.Trim().ToLowerInvariant();

    private static string Truncate(string value, int maxChars) =>
        string.IsNullOrWhiteSpace(value)
            ? null
            : value.Length <= maxChars ? value : value[..maxChars];

    private sealed record AttachmentFailureState(
        string Status,
        string NextAttemptAt);

    private const string StoredAttachmentSelectSql = """
        SELECT
            a.id,
            a.message_id,
            a.parent_attachment_id,
            a.root_attachment_id,
            a.source_kind,
            a.part_id,
            a.filename,
            a.display_path,
            a.archive_entry_path,
            a.mime_type,
            a.sniffed_mime_type,
            a.size_bytes,
            a.compressed_size_bytes,
            a.uncompressed_size_bytes,
            a.content_hash,
            a.storage_key,
            a.is_container,
            a.nesting_depth,
            a.download_status,
            a.download_error,
            a.extraction_status,
            a.extraction_error,
            a.extracted_text_available,
            a.ocr_text_available,
            a.extraction_error_code,
            a.extraction_next_attempt_at,
            a.extraction_completed_at,
            a.extractor,
            a.extractor_version
        FROM attachments a
        """;

    private const string AttachmentFailuresSql = """
        SELECT
            f.id AS failure_id,
            f.attachment_id,
            att.message_id,
            a.name AS account_name,
            att.display_path,
            COALESCE(att.sniffed_mime_type, att.mime_type) AS mime_type,
            f.stage,
            f.error_code,
            f.error_summary,
            attempt.exception_type,
            attempt.extractor,
            attempt.extractor_version,
            f.occurrence_count,
            f.first_seen_at,
            f.last_checked_at,
            f.resolved_at,
            f.status
        FROM attachment_extraction_failures f
        JOIN attachments att ON att.id = f.attachment_id
        JOIN messages m ON m.id = att.message_id
        JOIN accounts a ON a.id = m.account_id
        JOIN attachment_extraction_attempts attempt ON attempt.id = f.latest_attempt_id
        {WHERE
            {f.attachment_id [attachmentIds]}
            {m.account_id [accountIds]}
            {f.error_code [errorCodes]}
            {f.status [status]}}
        ORDER BY f.last_checked_at DESC, f.id DESC
        LIMIT $limit;
        """;

    private const string DueAttachmentExtractionsSql = """
        SELECT att.id
        FROM attachments att
        JOIN messages m ON m.id = att.message_id
        WHERE att.storage_key IS NOT NULL
          AND att.extraction_status IN ('pending', 'retry_wait')
          AND (att.extraction_next_attempt_at IS NULL OR att.extraction_next_attempt_at <= $now)
        {AND
            {m.account_id [accountIds]}}
        ORDER BY
            CASE WHEN att.extraction_attempts = 0 THEN 0 ELSE 1 END,
            COALESCE(att.extraction_next_attempt_at, att.created_at),
            att.id
        LIMIT $limit;
        """;

    private const string ScopedAttachmentFailureCountsSql = """
        SELECT failure.error_code, COUNT(DISTINCT failure.id)
        FROM attachment_extraction_failures failure
        JOIN attachments att ON att.id = failure.attachment_id
        JOIN messages m ON m.id = att.message_id
        JOIN accounts a ON a.id = m.account_id
        JOIN message_locations ml ON ml.message_id = m.id
        JOIN folders f ON f.id = ml.folder_id
        WHERE failure.status = 'open'
          AND a.enabled = 1
          AND f.selectable = 1
          AND f.sync_enabled = 1
          AND ml.deleted_locally = 0
          AND ml.expunged = 0
          AND ($dateFrom IS NULL OR COALESCE(m.date_sent, m.date_received) >= $dateFrom)
          AND ($dateTo IS NULL OR COALESCE(m.date_sent, m.date_received) <= $dateTo)
        {AND
            {m.account_id [accountIds]}
            {m.from_email COLLATE NOCASE [fromEmail]}
            {m.has_attachments [hasAttachments]}
            {LOWER(COALESCE(att.sniffed_mime_type, att.mime_type, '')) [mimeTypes]}
            {att.display_path [filenameContains]}
            {m.id IN (
                SELECT r_to.message_id
                FROM message_recipients r_to
                WHERE r_to.type = 'to'
                  {AND {r_to.email COLLATE NOCASE [toEmail]}}
            )}
            {f.role [folderRoles]}}
        GROUP BY failure.error_code
        ORDER BY failure.error_code;
        """;
}
