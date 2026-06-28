using Microsoft.Data.Sqlite;
using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.Json;

namespace LceMcp.Tests;

[Collection("Process-wide attachment extraction gate")]
public sealed class AttachmentReliabilityTests
{
    [Fact]
    public void UnsupportedTypeUsesTerminalFailureLifecycleAndDoesNotBlockReadiness()
    {
        using var temp = TempWorkspace.Create();
        var (database, messageId) = CreateMessage(temp);
        var processor = new AttachmentProcessor(new AttachmentObjectStore(temp.Paths));
        var attachment = processor.ProcessEmailAttachment(
            "2",
            "payload.bin",
            "application/octet-stream",
            4,
            [1, 2, 3, 4],
            "payload.bin");

        database.UpsertMessageBody(Body(messageId, attachment));

        var failure = Assert.Single(database.ListAttachmentExtractionFailures(OpenFailures()));
        var stored = database.ReadAttachmentText(failure.AttachmentId).Attachment;
        var readiness = database.GetMessageSearchReadiness(new(
            AccountFilters: ["yahoo"],
            FromEmail: null,
            FolderRoles: ["inbox"],
            HasAttachment: true,
            IncludeAttachments: true));

        Assert.Equal("failed", stored.ExtractionStatus);
        Assert.Equal("unsupported_attachment_type", stored.ExtractionErrorCode);
        Assert.Equal("unsupported_attachment_type", failure.ErrorCode);
        Assert.Equal(1, failure.OccurrenceCount);
        Assert.Empty(database.ReadDueAttachmentExtractionIds("yahoo", 10));
        Assert.True(readiness.AttachmentSearchIndexComplete);
        Assert.Equal(1, readiness.OpenAttachmentExtractionFailures);
        Assert.Equal(0, readiness.AttachmentTexts);
        Assert.Equal(1, readiness.AttachmentExtractionFailuresByCode["unsupported_attachment_type"]);
    }

    [Fact]
    public void RepeatedExplicitUnsupportedRetryRecordsAttemptButReusesOpenIssue()
    {
        using var temp = TempWorkspace.Create();
        var (database, messageId) = CreateMessage(temp);
        var processor = new AttachmentProcessor(new AttachmentObjectStore(temp.Paths));
        database.UpsertMessageBody(Body(messageId, processor.ProcessEmailAttachment(
            "2", "payload.bin", "application/octet-stream", 3, [1, 2, 3], "payload.bin")));
        var first = Assert.Single(database.ListAttachmentExtractionFailures(OpenFailures()));

        var result = new AttachmentExtractionRunner(database).RetryExplicit(
            OpenFailures() with
            {
                AccountFilters = ["yahoo"],
                ErrorCodes = ["unsupported_attachment_type"]
            },
            "test-client");
        var second = Assert.Single(database.ListAttachmentExtractionFailures(OpenFailures()));

        Assert.Equal(1, result.FailedCount);
        Assert.Equal(first.FailureId, second.FailureId);
        Assert.Equal(2, second.OccurrenceCount);
        Assert.Equal(2, ScalarInt(temp.Paths.DatabasePath, "SELECT COUNT(*) FROM attachment_extraction_attempts;"));
        Assert.Equal(1, ScalarInt(temp.Paths.DatabasePath, "SELECT COUNT(*) FROM attachment_extraction_failures WHERE status = 'open';"));
    }

    [Fact]
    public void FailedExtractorLogsFullExceptionAndSuccessfulRetryResolvesAndReindexes()
    {
        using var temp = TempWorkspace.Create();
        var (database, messageId) = CreateMessage(temp);
        var processor = new AttachmentProcessor(
            new AttachmentObjectStore(temp.Paths),
            _ => throw new InvalidOperationException("outer extractor failure", new IOException("inner I/O detail")));
        var failed = processor.ProcessEmailAttachment(
            "2",
            "recoverable.txt",
            "text/plain",
            7,
            Encoding.UTF8.GetBytes("ignored"),
            "recoverable.txt");
        database.UpsertMessageBody(Body(messageId, failed));
        var issue = Assert.Single(database.ListAttachmentExtractionFailures(OpenFailures()));
        var attachmentId = issue.AttachmentId;

        var result = new AttachmentExtractionRunner(
            database,
            _ => new("fixed searchable attachment text", "test-extractor", "2"))
            .RetryExplicit(OpenFailures([attachmentId]), "test-client");
        var resolved = Assert.Single(database.ListAttachmentExtractionFailures(
            OpenFailures([attachmentId]) with { Status = "resolved" }));
        var text = database.ReadAttachmentText(attachmentId);
        var log = File.ReadAllText(Path.Combine(temp.Paths.LogsDirectory, "attachment-extraction.log"));

        Assert.Equal(1, result.SucceededCount);
        Assert.Empty(database.ListAttachmentExtractionFailures(OpenFailures([attachmentId])));
        Assert.Equal(issue.FailureId, resolved.FailureId);
        Assert.Equal("resolved", resolved.Status);
        Assert.Contains("fixed searchable", text.CombinedText);
        Assert.Equal(attachmentId, text.Attachment.AttachmentId);
        Assert.Equal("test-extractor", text.Attachment.Extractor);
        Assert.Contains("InvalidOperationException", log);
        Assert.Contains("outer extractor failure", log);
        Assert.Contains("inner I/O detail", log);
        Assert.Contains($"attachment_id={attachmentId}", log);
    }

    [Fact]
    public void ChangedFailureClassificationSupersedesOldIssue()
    {
        using var temp = TempWorkspace.Create();
        var (database, messageId) = CreateMessage(temp);
        var processor = new AttachmentProcessor(
            new AttachmentObjectStore(temp.Paths),
            _ => throw new IOException("temporary"));
        database.UpsertMessageBody(Body(messageId, processor.ProcessEmailAttachment(
            "2", "document.txt", "text/plain", 4, Encoding.UTF8.GetBytes("data"), "document.txt")));
        var first = Assert.Single(database.ListAttachmentExtractionFailures(OpenFailures()));

        new AttachmentExtractionRunner(
            database,
            _ => throw new InvalidDataException("now corrupt"))
            .RetryExplicit(OpenFailures([first.AttachmentId]), "test-client");

        var open = Assert.Single(database.ListAttachmentExtractionFailures(OpenFailures([first.AttachmentId])));
        var superseded = Assert.Single(database.ListAttachmentExtractionFailures(
            OpenFailures([first.AttachmentId]) with { Status = "superseded" }));
        Assert.Equal("invalid_document", open.ErrorCode);
        Assert.Equal("temporary_io_failure", superseded.ErrorCode);
        Assert.NotEqual(open.FailureId, superseded.FailureId);
    }

    [Fact]
    public void TransientAutomaticRetriesBackOffAndStopAfterThreeAttempts()
    {
        using var temp = TempWorkspace.Create();
        var (database, messageId) = CreateMessage(temp);
        var processor = new AttachmentProcessor(
            new AttachmentObjectStore(temp.Paths),
            _ => throw new TimeoutException("slow extractor"));
        database.UpsertMessageBody(Body(messageId, processor.ProcessEmailAttachment(
            "2", "slow.txt", "text/plain", 4, Encoding.UTF8.GetBytes("data"), "slow.txt")));
        var issue = Assert.Single(database.ListAttachmentExtractionFailures(OpenFailures()));
        var now = DateTimeOffset.UtcNow.AddHours(1);

        SetDue(temp.Paths.DatabasePath, issue.AttachmentId, now);
        var second = new AttachmentExtractionRunner(
            database,
            _ => throw new TimeoutException("still slow"))
            .ProcessDue("yahoo", 10, now: now);
        var afterSecond = database.ReadAttachmentText(issue.AttachmentId).Attachment;

        SetDue(temp.Paths.DatabasePath, issue.AttachmentId, now.AddMinutes(1));
        var third = new AttachmentExtractionRunner(
            database,
            _ => throw new TimeoutException("still slow"))
            .ProcessDue("yahoo", 10, now: now.AddMinutes(1));
        var terminal = database.ReadAttachmentText(issue.AttachmentId).Attachment;
        var finalIssue = Assert.Single(database.ListAttachmentExtractionFailures(OpenFailures([issue.AttachmentId])));

        Assert.Equal(1, second.FailedCount);
        Assert.Equal("retry_wait", afterSecond.ExtractionStatus);
        Assert.NotNull(afterSecond.ExtractionNextAttemptAt);
        Assert.Equal(1, third.FailedCount);
        Assert.Equal("failed", terminal.ExtractionStatus);
        Assert.Null(terminal.ExtractionNextAttemptAt);
        Assert.Equal(3, finalIssue.OccurrenceCount);
        Assert.Equal(3, ScalarInt(temp.Paths.DatabasePath, "SELECT COUNT(*) FROM attachment_extraction_attempts;"));
    }

    [Fact]
    public void UnknownExtractorFailureGetsOneAutomaticRetryThenBecomesTerminal()
    {
        using var temp = TempWorkspace.Create();
        var (database, messageId) = CreateMessage(temp);
        var processor = new AttachmentProcessor(
            new AttachmentObjectStore(temp.Paths),
            _ => throw new InvalidOperationException("unexpected"));
        database.UpsertMessageBody(Body(messageId, processor.ProcessEmailAttachment(
            "2", "unknown.txt", "text/plain", 4, Encoding.UTF8.GetBytes("data"), "unknown.txt")));
        var issue = Assert.Single(database.ListAttachmentExtractionFailures(OpenFailures()));
        var now = DateTimeOffset.UtcNow.AddHours(1);
        SetDue(temp.Paths.DatabasePath, issue.AttachmentId, now);

        new AttachmentExtractionRunner(
            database,
            _ => throw new InvalidOperationException("unexpected again"))
            .ProcessDue("yahoo", 10, now: now);
        var terminal = database.ReadAttachmentText(issue.AttachmentId).Attachment;

        Assert.Equal("failed", terminal.ExtractionStatus);
        Assert.Equal("unknown_extractor_failure", terminal.ExtractionErrorCode);
        Assert.Equal(2, ScalarInt(temp.Paths.DatabasePath, "SELECT COUNT(*) FROM attachment_extraction_attempts;"));
        Assert.Empty(database.ReadDueAttachmentExtractionIds("yahoo", 10, now.AddHours(1)));
    }

    [Fact]
    public void ExpiredExtractionLeaseIsRecoveredAsRetryableWorkerCrash()
    {
        using var temp = TempWorkspace.Create();
        var (database, messageId) = CreateMessage(temp);
        var processor = new AttachmentProcessor(new AttachmentObjectStore(temp.Paths));
        database.UpsertMessageBody(Body(messageId, processor.ProcessEmailAttachment(
            "2", "payload.bin", "application/octet-stream", 3, [1, 2, 3], "payload.bin")));
        var issue = Assert.Single(database.ListAttachmentExtractionFailures(OpenFailures()));
        var start = DateTimeOffset.UtcNow.AddHours(1);
        var claim = database.ClaimAttachmentExtraction(issue.AttachmentId, "explicit_retry", "test", start);

        var recovered = database.RecoverExpiredAttachmentExtractions(start.AddMinutes(6));
        var attachment = database.ReadAttachmentText(issue.AttachmentId).Attachment;
        var open = Assert.Single(database.ListAttachmentExtractionFailures(OpenFailures([issue.AttachmentId])));

        Assert.NotNull(claim);
        Assert.Equal(1, recovered);
        Assert.Equal("retry_wait", attachment.ExtractionStatus);
        Assert.Equal("worker_crashed", attachment.ExtractionErrorCode);
        Assert.Equal("worker_crashed", open.ErrorCode);
        Assert.Single(database.ListAttachmentExtractionFailures(
            OpenFailures([issue.AttachmentId]) with { Status = "superseded" }));
    }

    [Fact]
    public void BoundedStreamRejectsBytesBeyondHardLimitWithoutGrowing()
    {
        using var stream = new BoundedWriteStream(4);
        stream.Write([1, 2, 3, 4]);

        var exception = Assert.Throws<AttachmentSizeLimitException>(() => stream.WriteByte(5));

        Assert.Contains("4 byte", exception.Message);
        Assert.Equal(4, stream.Length);
        Assert.Equal([1, 2, 3, 4], stream.ToArray());
    }

    [Fact]
    public void PreflightTooLargeAttachmentIsNotStoredAndUsesDeterministicFailure()
    {
        using var temp = TempWorkspace.Create();
        var processor = new AttachmentProcessor(new AttachmentObjectStore(temp.Paths));

        var rejected = processor.RejectEmailAttachment(
            "2",
            "huge.pdf",
            "application/pdf",
            AttachmentProcessor.MaxAttachmentBytes + 1,
            "huge.pdf",
            "attachment_too_large",
            "Declared size exceeds policy.");

        Assert.Equal("skipped", rejected.DownloadStatus);
        Assert.Equal("failed", rejected.ExtractionStatus);
        Assert.Equal("attachment_too_large", rejected.ExtractionErrorCode);
        Assert.Null(rejected.StorageKey);
        Assert.False(Directory.Exists(Path.Combine(temp.Paths.AttachmentsDirectory, "objects")));
    }

    [Fact]
    public void PersistedTooLargeDownloadIsTerminalAndDoesNotBlockReadiness()
    {
        using var temp = TempWorkspace.Create();
        var (database, messageId) = CreateMessage(temp);
        var processor = new AttachmentProcessor(new AttachmentObjectStore(temp.Paths));
        database.UpsertMessageBody(Body(messageId, processor.RejectEmailAttachment(
            "2",
            "huge.pdf",
            "application/pdf",
            AttachmentProcessor.MaxAttachmentBytes + 1,
            "huge.pdf",
            "attachment_too_large",
            "Declared size exceeds policy.")));

        var failure = Assert.Single(database.ListAttachmentExtractionFailures(OpenFailures()));
        var attachment = database.ReadAttachmentText(failure.AttachmentId).Attachment;
        var readiness = database.GetMessageSearchReadiness(new(
            AccountFilters: ["yahoo"],
            FromEmail: null,
            FolderRoles: ["inbox"],
            HasAttachment: true,
            IncludeAttachments: true));

        Assert.Equal("download", failure.Stage);
        Assert.Equal("failed", attachment.ExtractionStatus);
        Assert.Null(attachment.ExtractionNextAttemptAt);
        Assert.Equal(0, readiness.PendingAttachments);
        Assert.True(readiness.AttachmentSearchIndexComplete);
        Assert.Empty(database.ReadDueAttachmentExtractionIds("yahoo", 10));
    }

    [Fact]
    public void FailedRescanPreservesExistingObjectTextAndStableAttachmentId()
    {
        using var temp = TempWorkspace.Create();
        var (database, messageId) = CreateMessage(temp);
        var processor = new AttachmentProcessor(new AttachmentObjectStore(temp.Paths));
        var successful = processor.ProcessEmailAttachment(
            "2",
            "invoice.txt",
            "text/plain",
            12,
            Encoding.UTF8.GetBytes("invoice text"),
            "invoice.txt");
        database.UpsertMessageBody(Body(messageId, successful));
        var original = database.ReadAttachmentText(
            database.SearchMessages(new(
                "invoice",
                ["yahoo"],
                null,
                ["inbox"],
                true,
                10,
                1024,
                SearchIn: ["attachments"]))
                .Single()
                .MatchingAttachments
                .Single()
                .Attachment
                .AttachmentId);

        database.UpsertMessageBody(Body(messageId, processor.RejectEmailAttachment(
            "2",
            "invoice.txt",
            "text/plain",
            12,
            "invoice.txt",
            "temporary_io_failure",
            "Temporary provider read failure.",
            new IOException("provider read failed"))));

        var afterFailure = database.ReadAttachmentText(original.Attachment.AttachmentId);
        var issue = Assert.Single(database.ListAttachmentExtractionFailures(OpenFailures(
            [original.Attachment.AttachmentId])));

        Assert.Equal(original.Attachment.AttachmentId, afterFailure.Attachment.AttachmentId);
        Assert.Equal(original.Attachment.StorageKey, afterFailure.Attachment.StorageKey);
        Assert.Equal("done", afterFailure.Attachment.ExtractionStatus);
        Assert.Equal(original.CombinedText, afterFailure.CombinedText);
        Assert.Equal("download", issue.Stage);
        Assert.Equal("temporary_io_failure", issue.ErrorCode);

        var localRetry = new AttachmentExtractionRunner(database)
            .RetryExplicit(OpenFailures([original.Attachment.AttachmentId]), "test");
        Assert.Equal(0, localRetry.SelectedCount);
        Assert.Single(database.ListAttachmentExtractionFailures(OpenFailures(
            [original.Attachment.AttachmentId])));

        database.UpsertMessageBody(Body(messageId, successful));

        Assert.Empty(database.ListAttachmentExtractionFailures(OpenFailures(
            [original.Attachment.AttachmentId])));
        var resolvedDownload = Assert.Single(database.ListAttachmentExtractionFailures(
            OpenFailures([original.Attachment.AttachmentId]) with { Status = "resolved" }));
        Assert.Equal("download", resolvedDownload.Stage);
        Assert.Null(database.ReadAttachmentText(original.Attachment.AttachmentId).Attachment.DownloadError);
    }

    [Fact]
    public void FailedArchiveRescanPreservesPreviouslyIndexedDescendants()
    {
        using var temp = TempWorkspace.Create();
        var (database, messageId) = CreateMessage(temp);
        var processor = new AttachmentProcessor(new AttachmentObjectStore(temp.Paths));
        var archive = processor.ProcessEmailAttachment(
            "2",
            "documents.zip",
            "application/zip",
            null,
            CreateZip(("inside.txt", "searchable archive child")),
            "documents.zip");
        database.UpsertMessageBody(Body(messageId, archive));
        var child = Assert.Single(database.ReadMessage(messageId).Attachments, item => item.ParentAttachmentId is not null);

        database.UpsertMessageBody(Body(messageId, processor.RejectEmailAttachment(
            "2",
            "documents.zip",
            "application/zip",
            archive.SizeBytes,
            "documents.zip",
            "temporary_io_failure",
            "Temporary provider read failure.",
            new IOException("provider read failed"))));

        var attachments = database.ReadMessage(messageId).Attachments;
        Assert.Equal(2, attachments.Count);
        Assert.Contains(attachments, item => item.AttachmentId == child.AttachmentId);
        Assert.Contains("searchable archive child", database.ReadAttachmentText(child.AttachmentId).CombinedText);
    }

    [Fact]
    public void ExpiredLeaseCannotCompleteAndStatusReadRecoversIt()
    {
        using var temp = TempWorkspace.Create();
        var (database, messageId) = CreateMessage(temp);
        var processor = new AttachmentProcessor(new AttachmentObjectStore(temp.Paths));
        database.UpsertMessageBody(Body(messageId, processor.ProcessEmailAttachment(
            "2", "payload.bin", "application/octet-stream", 3, [1, 2, 3], "payload.bin")));
        var issue = Assert.Single(database.ListAttachmentExtractionFailures(OpenFailures()));
        var claim = database.ClaimAttachmentExtraction(
            issue.AttachmentId,
            "explicit_retry",
            "test",
            DateTimeOffset.UtcNow.AddMinutes(-10));
        var success = processor.ProcessStoredAttachment(
            claim.Attachment,
            Encoding.UTF8.GetBytes("fixed"));

        var completed = database.CompleteAttachmentExtraction(claim, success);
        var attachment = database.ReadAttachmentText(issue.AttachmentId).Attachment;

        Assert.False(completed);
        Assert.NotEqual("running", attachment.ExtractionStatus);
        Assert.Equal("worker_crashed", attachment.ExtractionErrorCode);
    }

    [Fact]
    public void CanceledExplicitRetryDoesNotLeaveRunningClaim()
    {
        using var temp = TempWorkspace.Create();
        var (database, messageId) = CreateMessage(temp);
        var processor = new AttachmentProcessor(new AttachmentObjectStore(temp.Paths));
        database.UpsertMessageBody(Body(messageId, processor.ProcessEmailAttachment(
            "2", "payload.bin", "application/octet-stream", 3, [1, 2, 3], "payload.bin")));
        var issue = Assert.Single(database.ListAttachmentExtractionFailures(OpenFailures()));
        var runner = new AttachmentExtractionRunner(
            database,
            _ => throw new OperationCanceledException("test cancellation"));

        Assert.Throws<OperationCanceledException>(() =>
            runner.RetryExplicit(OpenFailures([issue.AttachmentId]), "test"));

        var attachment = database.ReadAttachmentText(issue.AttachmentId).Attachment;
        Assert.NotEqual("running", attachment.ExtractionStatus);
        Assert.Equal("worker_canceled", attachment.ExtractionErrorCode);
    }

    [Fact]
    public void NewBodyScanSupersedesRunningRetryAndStaleWorkerCannotOverwriteIt()
    {
        using var temp = TempWorkspace.Create();
        var (database, messageId) = CreateMessage(temp);
        var processor = new AttachmentProcessor(new AttachmentObjectStore(temp.Paths));
        database.UpsertMessageBody(Body(messageId, processor.ProcessEmailAttachment(
            "2", "payload.bin", "application/octet-stream", 3, [1, 2, 3], "payload.bin")));
        var issue = Assert.Single(database.ListAttachmentExtractionFailures(OpenFailures()));
        var claim = database.ClaimAttachmentExtraction(issue.AttachmentId, "explicit_retry", "test");
        var staleResult = processor.ProcessStoredAttachment(claim.Attachment, Encoding.UTF8.GetBytes("stale text"));

        database.UpsertMessageBody(Body(messageId, processor.ProcessEmailAttachment(
            "2",
            "payload.bin",
            "text/plain",
            10,
            Encoding.UTF8.GetBytes("fresh text"),
            "payload.bin")));

        Assert.False(database.CompleteAttachmentExtraction(claim, staleResult));
        var current = database.ReadAttachmentText(issue.AttachmentId);
        Assert.Equal("fresh text", current.CombinedText);
        Assert.Equal("done", current.Attachment.ExtractionStatus);
        Assert.Equal("superseded", ScalarString(
            temp.Paths.DatabasePath,
            $"SELECT outcome FROM attachment_extraction_attempts WHERE id = {claim.AttemptId};"));
    }

    [Fact]
    public void BodyFailuresExhaustAndRequireExplicitRequeue()
    {
        using var temp = TempWorkspace.Create();
        var (database, messageId) = CreateMessage(temp);

        for (var attempt = 0; attempt < 3; attempt++)
            database.RecordBodySyncFailure(
                messageId,
                "temporary_io_failure",
                "Provider read failed.",
                new IOException("provider read failed"));

        Assert.Empty(database.ReadPendingBodySyncTargets("yahoo", "Inbox", 0));
        Assert.Equal(1, ScalarInt(
            temp.Paths.DatabasePath,
            $"SELECT body_retry_exhausted FROM messages WHERE id = {messageId};"));

        var requeued = database.RequeueExhaustedBodyRetries("yahoo", "Inbox");

        Assert.Equal(1, requeued);
        Assert.Single(database.ReadPendingBodySyncTargets("yahoo", "Inbox", 0));
        Assert.Equal(0, ScalarInt(
            temp.Paths.DatabasePath,
            $"SELECT body_attempts FROM messages WHERE id = {messageId};"));
    }

    [Fact]
    public void TerminalExtractorTimeoutReturnsBoundedFailure()
    {
        using var temp = TempWorkspace.Create();
        var processor = new AttachmentProcessor(
            new AttachmentObjectStore(temp.Paths),
            _ =>
            {
                Thread.Sleep(150);
                return new("late text", "slow-test");
            },
            extractionTimeout: TimeSpan.FromMilliseconds(20));
        var started = Stopwatch.StartNew();

        var attachment = processor.ProcessEmailAttachment(
            "2",
            "slow.txt",
            "text/plain",
            4,
            Encoding.UTF8.GetBytes("data"),
            "slow.txt");

        Assert.Equal("failed", attachment.ExtractionStatus);
        Assert.Equal("extractor_timeout", attachment.ExtractionErrorCode);
        Assert.True(started.Elapsed < TimeSpan.FromMilliseconds(120));
        Thread.Sleep(160);
    }

    [Fact]
    public void SyncFailureAccountingIncludesStoredAndSkippedExtractionFailures()
    {
        using var temp = TempWorkspace.Create();
        var processor = new AttachmentProcessor(new AttachmentObjectStore(temp.Paths));
        var unsupported = processor.ProcessEmailAttachment(
            "2", "payload.bin", "application/octet-stream", 3, [1, 2, 3], "payload.bin");
        var skipped = processor.RejectEmailAttachment(
            "3",
            "huge.pdf",
            "application/pdf",
            AttachmentProcessor.MaxAttachmentBytes + 1,
            "huge.pdf",
            "attachment_too_large",
            "Too large.");

        Assert.True(ImapBodySync.HasAttachmentFailures([unsupported]));
        Assert.True(ImapBodySync.HasAttachmentFailures([skipped]));
    }

    [Fact]
    public void DiagnosticLoggingFailureDoesNotEscape()
    {
        using var temp = TempWorkspace.Create();
        var logger = new AttachmentDiagnosticLogger(temp.Paths, new ThrowingTextWriter());

        var exception = Record.Exception(() =>
            logger.Write(1, "extraction", "initial", "unknown_extractor_failure", "details"));

        Assert.Null(exception);
    }

    [Fact]
    public void BodyTargetSelectionSkipsBackoffAndPrioritizesNeverAttemptedWork()
    {
        using var temp = TempWorkspace.Create();
        var database = new EmailDatabase(temp.Paths);
        var accountId = database.UpsertConfiguredAccount(TestData.Account());
        database.UpsertFolders(accountId, [TestData.Folder("Inbox", role: "inbox")]);
        var inbox = database.ReadFolders("yahoo").Single();
        database.UpsertMessageMetadataBatch(accountId, inbox.Id, [
            TestData.Message("100", "emailid:old", "old@example.com", dateSent: "2026-06-20T10:00:00Z"),
            TestData.Message("101", "emailid:middle", "middle@example.com", dateSent: "2026-06-21T10:00:00Z"),
            TestData.Message("102", "emailid:new", "new@example.com", dateSent: "2026-06-22T10:00:00Z")
        ], SyncState(3), 102);
        Execute(
            temp.Paths.DatabasePath,
            "UPDATE messages SET body_attempts = 1, body_next_attempt_at = '2999-01-01T00:00:00Z' WHERE provider_message_key = 'emailid:new';");

        var selected = Assert.Single(database.ReadPendingBodySyncTargets("yahoo", "Inbox", 1));

        Assert.Equal("101", selected.ProviderUid);
    }

    [Fact]
    public void MigrationConvertsLegacyUnsupportedRowWithoutChangingIdentityOrStorage()
    {
        using var temp = TempWorkspace.Create();
        temp.Paths.EnsureDataDirectories();
        Execute(
            temp.Paths.DatabasePath,
            """
            CREATE TABLE schema_migrations (
                version INTEGER PRIMARY KEY,
                name TEXT NOT NULL,
                applied_at TEXT NOT NULL
            );
            """);

        foreach (var migration in DatabaseMigrations.All.Where(item => item.Version <= 7))
        {
            Execute(temp.Paths.DatabasePath, migration.Sql);
            Execute(
                temp.Paths.DatabasePath,
                $"INSERT INTO schema_migrations(version, name, applied_at) VALUES ({migration.Version}, '{migration.Name}', '2026-06-27T00:00:00Z');");
        }

        Execute(
            temp.Paths.DatabasePath,
            """
            INSERT INTO accounts (
                id, name, email_address, imap_host, imap_port, imap_security,
                username, created_at
            )
            VALUES (
                1, 'yahoo', 'person@yahoo.com', 'imap.example.com', 993, 'ssl',
                'person@yahoo.com', '2026-06-27T00:00:00Z'
            );

            INSERT INTO messages (
                id, account_id, has_attachments, created_at, updated_at
            )
            VALUES (
                9, 1, 1, '2026-06-27T00:00:00Z', '2026-06-27T00:00:00Z'
            );

            INSERT INTO attachments (
                id, message_id, display_path, storage_key, download_status,
                extraction_status, extraction_error, created_at, updated_at
            )
            VALUES (
                42, 9, 'legacy.7z',
                'objects/aa/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa',
                'stored', 'unsupported', '7z was not supported',
                '2026-06-27T00:00:00Z', '2026-06-27T00:00:00Z'
            );
            """);

        var database = new EmailDatabase(temp.Paths);
        var status = database.GetStatus();
        var attachment = database.ReadAttachmentText(42).Attachment;
        var failure = Assert.Single(database.ListAttachmentExtractionFailures(OpenFailures([42])));

        Assert.Equal(DatabaseInitializationKind.Migrated, status.InitializationKind);
        Assert.Equal(9, status.SchemaVersion);
        Assert.Equal(42, attachment.AttachmentId);
        Assert.Equal("failed", attachment.ExtractionStatus);
        Assert.Equal("unsupported_attachment_type", attachment.ExtractionErrorCode);
        Assert.NotNull(attachment.StorageKey);
        Assert.Equal("unsupported_attachment_type", failure.ErrorCode);
        Assert.Equal("legacy_migration", ScalarString(
            temp.Paths.DatabasePath,
            "SELECT trigger_kind FROM attachment_extraction_attempts WHERE attachment_id = 42;"));
    }

    private static (EmailDatabase Database, int MessageId) CreateMessage(TempWorkspace temp)
    {
        var database = new EmailDatabase(temp.Paths);
        var accountId = database.UpsertConfiguredAccount(TestData.Account());
        database.UpsertFolders(accountId, [TestData.Folder("Inbox", role: "inbox")]);
        var inbox = database.ReadFolders("yahoo").Single();
        database.UpsertMessageMetadataBatch(accountId, inbox.Id, [
            TestData.Message(
                "100",
                "emailid:attachment-reliability",
                "attachment-reliability@example.com",
                hasAttachments: true)
        ], SyncState(1), 100);
        return (database, ScalarInt(temp.Paths.DatabasePath, "SELECT id FROM messages;"));
    }

    private static MessageBodyContent Body(int messageId, AttachmentContent attachment) =>
        new(messageId, "Body", null, "Body", [], [attachment]);

    private static AttachmentExtractionFailureQuery OpenFailures(IReadOnlyList<int> attachmentIds = null) =>
        new(attachmentIds ?? [], [], [], "open", 20);

    private static string SyncState(int count) =>
        JsonSerializer.Serialize(new Dictionary<string, int>
        {
            ["since_days"] = 30,
            ["max_per_folder"] = 0,
            ["matched_count"] = count,
            ["selected_count"] = count,
            ["fetched_count"] = count,
            ["missing_count"] = 0
        });

    private static byte[] CreateZip(params (string Path, string Content)[] entries)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var entry in entries)
            {
                var zipEntry = archive.CreateEntry(entry.Path);
                using var writer = new StreamWriter(zipEntry.Open(), Encoding.UTF8);
                writer.Write(entry.Content);
            }
        }

        return stream.ToArray();
    }

    private static void SetDue(string path, int attachmentId, DateTimeOffset now) =>
        Execute(
            path,
            $"UPDATE attachments SET extraction_next_attempt_at = '{now.AddSeconds(-1):O}' WHERE id = {attachmentId};");

    private static int ScalarInt(string path, string sql)
    {
        using var connection = Open(path);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt32(command.ExecuteScalar());
    }

    private static string ScalarString(string path, string sql)
    {
        using var connection = Open(path);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(command.ExecuteScalar());
    }

    private static void Execute(string path, string sql)
    {
        using var connection = Open(path);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static SqliteConnection Open(string path)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Pooling = false
        }.ToString());
        connection.Open();
        return connection;
    }

    private sealed class ThrowingTextWriter : TextWriter
    {
        public override Encoding Encoding => Encoding.UTF8;

        public override void Write(char value) =>
            throw new IOException("writer unavailable");

        public override void Write(string value) =>
            throw new IOException("writer unavailable");

        public override void WriteLine(string value) =>
            throw new IOException("writer unavailable");
    }
}
