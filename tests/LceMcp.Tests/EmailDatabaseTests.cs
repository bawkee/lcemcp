using Microsoft.Data.Sqlite;
using System.Text;
using System.Text.Json;

namespace LceMcp.Tests;

public sealed class EmailDatabaseTests
{
    [Fact]
    public void GetStatusInitializesMigrationSchema()
    {
        using var temp = TempWorkspace.Create();
        var database = new EmailDatabase(temp.Paths);

        var status = database.GetStatus();

        Assert.Equal(DatabaseInitializationKind.Created, status.InitializationKind);
        Assert.Equal(9, status.SchemaVersion);
        Assert.Equal(9, status.TargetSchemaVersion);
        Assert.Equal(0, status.AccountCount);
        Assert.Equal(0, status.FolderCount);
        Assert.Equal(0, status.MessageCount);
        Assert.Equal(0, status.MessageLocationCount);
        Assert.Equal(0, status.MessageBodyCount);
        Assert.Equal(0, status.MessageSearchDocCount);
        Assert.Equal(0, status.AttachmentCount);
        Assert.Equal(0, status.AttachmentTextCount);
        Assert.Equal(0, status.AttachmentSearchDocCount);
        Assert.Null(status.LastSyncState);
        Assert.True(File.Exists(temp.Paths.DatabasePath));
        Assert.True(Directory.Exists(temp.Paths.AttachmentsDirectory));
        Assert.True(Directory.Exists(temp.Paths.LogsDirectory));

        var tableNames = ReadNames(
            temp.Paths.DatabasePath,
            "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' ORDER BY name;").ToHashSet();

        foreach (var tableName in new[]
        {
            "accounts",
            "audit_log",
            "attachment_search_docs",
            "attachment_extraction_attempts",
            "attachment_extraction_failures",
            "attachment_text",
            "attachments",
            "attachments_fts",
            "folders",
            "message_bodies",
            "message_locations",
            "message_recipients",
            "message_search_docs",
            "messages",
            "messages_fts",
            "schema_migrations",
            "sync_leases",
            "sync_runs",
            "sync_state"
        })
            Assert.Contains(tableName, tableNames);
    }

    [Fact]
    public void GetStatusOpensAlreadyMigratedDatabase()
    {
        using var temp = TempWorkspace.Create();
        var database = new EmailDatabase(temp.Paths);

        database.GetStatus();
        var status = database.GetStatus();

        Assert.Equal(DatabaseInitializationKind.Opened, status.InitializationKind);
        Assert.Equal(9, status.SchemaVersion);
        Assert.Equal(["initial_metadata_cache", "message_bodies_and_search", "sync_runs_and_search_readiness", "sync_queue_and_leases", "sync_window_tracking", "attachment_metadata_and_search", "attachment_scan_tracking", "attachment_processing_reliability", "bounded_body_retries"], ReadNames(
            temp.Paths.DatabasePath,
            "SELECT name FROM schema_migrations ORDER BY version;"));
    }

    [Fact]
    public void GetStatusMigratesVersionOneDatabaseToBodySearchSchema()
    {
        using var temp = TempWorkspace.Create();
        temp.Paths.EnsureDataDirectories();

        ExecuteNonQuery(
            temp.Paths.DatabasePath,
            """
            CREATE TABLE schema_migrations (
                version INTEGER PRIMARY KEY,
                name TEXT NOT NULL,
                applied_at TEXT NOT NULL
            );

            INSERT INTO schema_migrations (version, name, applied_at)
            VALUES (1, 'initial_metadata_cache', '2026-06-19T00:00:00.0000000+00:00');
            """);
        ExecuteNonQuery(temp.Paths.DatabasePath, DatabaseMigrations.InitialSchemaSql);

        var database = new EmailDatabase(temp.Paths);
        var status = database.GetStatus();

        Assert.Equal(DatabaseInitializationKind.Migrated, status.InitializationKind);
        Assert.Equal(9, status.SchemaVersion);
        Assert.Equal(9, status.TargetSchemaVersion);
        Assert.Equal(["initial_metadata_cache", "message_bodies_and_search", "sync_runs_and_search_readiness", "sync_queue_and_leases", "sync_window_tracking", "attachment_metadata_and_search", "attachment_scan_tracking", "attachment_processing_reliability", "bounded_body_retries"], ReadNames(
            temp.Paths.DatabasePath,
            "SELECT name FROM schema_migrations ORDER BY version;"));
        Assert.Contains("message_search_docs", ReadNames(
            temp.Paths.DatabasePath,
            "SELECT name FROM sqlite_master WHERE type = 'table' ORDER BY name;"));
        Assert.Contains("sync_runs", ReadNames(
            temp.Paths.DatabasePath,
            "SELECT name FROM sqlite_master WHERE type = 'table' ORDER BY name;"));
        Assert.Contains("attachments_scanned", ReadNames(
            temp.Paths.DatabasePath,
            "SELECT name FROM pragma_table_info('messages') ORDER BY cid;"));
        Assert.Contains("sync_leases", ReadNames(
            temp.Paths.DatabasePath,
            "SELECT name FROM sqlite_master WHERE type = 'table' ORDER BY name;"));
    }

    [Fact]
    public void UpsertConfiguredAccountAndFoldersAreIdempotent()
    {
        using var temp = TempWorkspace.Create();
        var database = new EmailDatabase(temp.Paths);

        var firstAccountId = database.UpsertConfiguredAccount(TestData.Account());
        var secondAccountId = database.UpsertConfiguredAccount(TestData.Account(
            imapHost: "imap.changed.example",
            imapSecurity: "starttls"));
        var persisted = database.UpsertFolders(secondAccountId, [
            TestData.Folder("Inbox", role: "inbox", attributes: @"\Inbox", messageCount: 10),
            TestData.Folder("Archive", role: "archive", attributes: @"\Archive", messageCount: 20)
        ]);
        database.UpsertFolders(secondAccountId, [
            TestData.Folder("Inbox", role: "inbox", attributes: @"\Inbox \HasNoChildren", messageCount: 11),
            TestData.Folder("Archive", role: "archive", attributes: @"\Archive", messageCount: 20)
        ]);

        var status = database.GetStatus();
        var folders = database.ReadFolders("YAHOO");
        var inbox = Assert.Single(folders, folder => folder.Path == "Inbox");
        var archive = Assert.Single(folders, folder => folder.Path == "Archive");

        Assert.Equal(firstAccountId, secondAccountId);
        Assert.Equal(2, persisted);
        Assert.Equal(1, status.AccountCount);
        Assert.Equal(2, status.FolderCount);
        Assert.Equal(@"\Inbox \HasNoChildren", inbox.Attributes);
        Assert.Equal(11, inbox.MessageCount);
        Assert.True(inbox.SyncEnabled);
        Assert.True(archive.SyncEnabled);
    }

    [Fact]
    public void FolderDiscoveryUsesRoleDefaultsAndPreservesExistingSyncChoices()
    {
        using var temp = TempWorkspace.Create();
        var database = new EmailDatabase(temp.Paths);
        var accountId = database.UpsertConfiguredAccount(TestData.Account());

        database.UpsertFolders(accountId, [
            TestData.Folder("Inbox", role: "inbox"),
            TestData.Folder("Trash", role: "trash"),
            TestData.Folder("Custom", role: "custom")
        ]);

        Assert.Equal(1, database.SetFolderSyncEnabled("yahoo", "Trash", enabled: true));
        database.UpsertFolders(accountId, [
            TestData.Folder("Inbox", role: "inbox", attributes: @"\Inbox \HasNoChildren"),
            TestData.Folder("Trash", role: "trash", attributes: @"\Trash \HasNoChildren"),
            TestData.Folder("Custom", role: "custom", attributes: @"\HasNoChildren")
        ]);

        var folders = database.ReadFolders("yahoo");
        var inbox = Assert.Single(folders, folder => folder.Path == "Inbox");
        var trash = Assert.Single(folders, folder => folder.Path == "Trash");
        var custom = Assert.Single(folders, folder => folder.Path == "Custom");

        Assert.True(inbox.SyncEnabled);
        Assert.True(trash.SyncEnabled);
        Assert.False(custom.SyncEnabled);
        Assert.Equal(@"\Trash \HasNoChildren", trash.Attributes);
    }

    [Fact]
    public void ReadSyncFoldersAllowsExplicitDisabledSelectableFolder()
    {
        using var temp = TempWorkspace.Create();
        var database = new EmailDatabase(temp.Paths);
        var accountId = database.UpsertConfiguredAccount(TestData.Account());

        database.UpsertFolders(accountId, [
            TestData.Folder("Inbox", role: "inbox"),
            TestData.Folder("Important", role: "custom"),
            TestData.Folder("Container", role: "custom", selectable: false)
        ]);

        var defaultFolders = database.ReadSyncFolders("yahoo", folderFilter: null);
        var explicitImportant = database.ReadSyncFolders("yahoo", "Important");
        var explicitContainer = database.ReadSyncFolders("yahoo", "Container");

        var important = Assert.Single(explicitImportant);
        Assert.Equal(["Inbox"], defaultFolders.Select(folder => folder.Path).ToArray());
        Assert.Equal("Important", important.Path);
        Assert.False(important.SyncEnabled);
        Assert.Empty(explicitContainer);
    }

    [Fact]
    public void UpsertMessageMetadataBatchIsIdempotentByLocationAndProviderMessageKey()
    {
        using var temp = TempWorkspace.Create();
        var database = new EmailDatabase(temp.Paths);
        var accountId = database.UpsertConfiguredAccount(TestData.Account());
        database.UpsertFolders(accountId, [
            TestData.Folder("Inbox", role: "inbox"),
            TestData.Folder("Archive", role: "archive")
        ]);
        var inbox = database.ReadFolders("yahoo").Single(folder => folder.Path == "Inbox");
        var archive = database.ReadFolders("yahoo").Single(folder => folder.Path == "Archive");

        database.UpsertMessageMetadataBatch(accountId, inbox.Id, [
            TestData.Message(providerUid: "100", providerMessageKey: "emailid:abc", messageIdHeader: "abc@example.com")
        ], """{"batch":1}""", 100);
        database.UpsertMessageMetadataBatch(accountId, inbox.Id, [
            TestData.Message(
                providerUid: "100",
                providerMessageKey: "emailid:abc",
                messageIdHeader: "abc@example.com",
                subject: "Updated subject",
                flags: @"\Seen")
        ], """{"batch":2}""", 100);
        database.UpsertMessageMetadataBatch(accountId, archive.Id, [
            TestData.Message(providerUid: "44", providerMessageKey: "emailid:abc", messageIdHeader: "abc@example.com", subject: null)
        ], """{"batch":3}""", 44);

        var status = database.GetStatus();

        Assert.Equal(1, status.MessageCount);
        Assert.Equal(2, status.MessageLocationCount);
        Assert.Equal("yahoo/Archive", $"{status.LastSyncState.AccountName}/{status.LastSyncState.FolderPath}");
        Assert.Equal(["Updated subject"], ReadNames(
            temp.Paths.DatabasePath,
            "SELECT subject FROM messages ORDER BY id;"));
        Assert.Equal([@"\Seen", null], ReadNullableNames(
            temp.Paths.DatabasePath,
            "SELECT flags FROM message_locations ORDER BY folder_id;"));
        Assert.Equal([100L, 44L], ReadLongs(
            temp.Paths.DatabasePath,
            "SELECT last_uid FROM folders ORDER BY path DESC;"));
    }

    [Fact]
    public void UpsertMessageMetadataBatchFallsBackToMessageIdHeader()
    {
        using var temp = TempWorkspace.Create();
        var database = new EmailDatabase(temp.Paths);
        var accountId = database.UpsertConfiguredAccount(TestData.Account());
        database.UpsertFolders(accountId, [
            TestData.Folder("Inbox", role: "inbox"),
            TestData.Folder("Sent", role: "sent")
        ]);
        var inbox = database.ReadFolders("yahoo").Single(folder => folder.Path == "Inbox");
        var sent = database.ReadFolders("yahoo").Single(folder => folder.Path == "Sent");

        database.UpsertMessageMetadataBatch(accountId, inbox.Id, [
            TestData.Message(providerUid: "10", providerMessageKey: null, messageIdHeader: "same@example.com")
        ], """{"batch":1}""", 10);
        database.UpsertMessageMetadataBatch(accountId, sent.Id, [
            TestData.Message(providerUid: "20", providerMessageKey: null, messageIdHeader: "same@example.com")
        ], """{"batch":2}""", 20);

        var status = database.GetStatus();

        Assert.Equal(1, status.MessageCount);
        Assert.Equal(2, status.MessageLocationCount);
    }

    [Fact]
    public void UpsertMessageMetadataBatchRollsBackWhenLocationInsertFails()
    {
        using var temp = TempWorkspace.Create();
        var database = new EmailDatabase(temp.Paths);
        var accountId = database.UpsertConfiguredAccount(TestData.Account());

        Assert.Throws<SqliteException>(() => database.UpsertMessageMetadataBatch(accountId, folderId: 999, [
            TestData.Message(providerUid: "100", providerMessageKey: "emailid:abc")
        ], """{"batch":1}""", 100));

        var status = database.GetStatus();
        Assert.Equal(0, status.MessageCount);
        Assert.Equal(0, status.MessageLocationCount);
        Assert.Null(status.LastSyncState);
    }

    [Fact]
    public void UpsertMessageBodyRefreshesSearchIndex()
    {
        using var temp = TempWorkspace.Create();
        var database = new EmailDatabase(temp.Paths);
        var accountId = database.UpsertConfiguredAccount(TestData.Account());
        database.UpsertFolders(accountId, [
            TestData.Folder("Inbox", role: "inbox")
        ]);
        var inbox = database.ReadFolders("yahoo").Single(folder => folder.Path == "Inbox");

        database.UpsertMessageMetadataBatch(accountId, inbox.Id, [
            TestData.Message(
                providerUid: "100",
                providerMessageKey: "emailid:abc",
                subject: "Refund update")
        ], """{"batch":1}""", 100);
        var messageId = ReadInts(temp.Paths.DatabasePath, "SELECT id FROM messages;").Single();

        database.UpsertMessageBody(new(
            MessageId: messageId,
            PlainText: "Hello, the refund processed yesterday.",
            HtmlText: null,
            NormalizedText: "Hello, the refund processed yesterday.",
            Recipients: [
                new("to", "Customer", "customer@example.com")
            ]));

        var firstStatus = database.GetStatus();
        var refundResults = database.SearchMessages(new(
            Query: "\"refund processed\"",
            AccountFilters: ["yahoo"],
            FromEmail: null,
            FolderRoles: ["inbox"],
            HasAttachment: null,
            Limit: 10,
            SnippetChars: 1024));
        var recipientResults = database.SearchMessages(new(
            Query: "customer@example.com",
            AccountFilters: null,
            FromEmail: null,
            FolderRoles: null,
            HasAttachment: false,
            Limit: 10,
            SnippetChars: 1024));

        database.UpsertMessageBody(new(
            MessageId: messageId,
            PlainText: "Chargeback closed with no refund mention.",
            HtmlText: null,
            NormalizedText: "Chargeback closed with no refund mention.",
            Recipients: []));

        var staleResults = database.SearchMessages(new(
            Query: "\"refund processed\"",
            AccountFilters: ["yahoo"],
            FromEmail: null,
            FolderRoles: ["inbox"],
            HasAttachment: null,
            Limit: 10,
            SnippetChars: 1024));
        var updatedResults = database.SearchMessages(new(
            Query: "chargeback",
            AccountFilters: ["person@yahoo.com"],
            FromEmail: "sender@example.com",
            FolderRoles: ["inbox"],
            HasAttachment: false,
            Limit: 10,
            SnippetChars: 1024));

        var refundResult = Assert.Single(refundResults);
        Assert.Equal(1, firstStatus.MessageBodyCount);
        Assert.Equal(1, firstStatus.MessageSearchDocCount);
        Assert.Contains("refund", refundResult.Snippet, StringComparison.OrdinalIgnoreCase);
        Assert.Single(recipientResults);
        Assert.Empty(staleResults);
        Assert.Single(updatedResults);
    }

    [Fact]
    public void UpsertMessageBodiesRefreshesSearchIndexInOneBatch()
    {
        using var temp = TempWorkspace.Create();
        var database = new EmailDatabase(temp.Paths);
        var accountId = database.UpsertConfiguredAccount(TestData.Account());
        database.UpsertFolders(accountId, [
            TestData.Folder("Inbox", role: "inbox")
        ]);
        var inbox = database.ReadFolders("yahoo").Single(folder => folder.Path == "Inbox");

        database.UpsertMessageMetadataBatch(accountId, inbox.Id, [
            TestData.Message(
                providerUid: "100",
                providerMessageKey: "emailid:batch-a",
                messageIdHeader: "batch-a@example.com",
                subject: "Batch Alpha"),
            TestData.Message(
                providerUid: "101",
                providerMessageKey: "emailid:batch-b",
                messageIdHeader: "batch-b@example.com",
                subject: "Batch Beta")
        ], """{"batch":1}""", 101);
        var messageIds = ReadInts(temp.Paths.DatabasePath, "SELECT id FROM messages ORDER BY id;");

        database.UpsertMessageBodies([
            new(
                MessageId: messageIds[0],
                PlainText: "alpha body",
                HtmlText: null,
                NormalizedText: "alpha body",
                Recipients: [new("to", "Alpha", "alpha@example.com")]),
            new(
                MessageId: messageIds[1],
                PlainText: "beta body",
                HtmlText: null,
                NormalizedText: "beta body",
                Recipients: [new("to", "Beta", "beta@example.com")])
        ]);

        var status = database.GetStatus();
        var alphaResults = database.SearchMessages(new(
            Query: "alpha@example.com",
            AccountFilters: ["yahoo"],
            FromEmail: null,
            FolderRoles: ["inbox"],
            HasAttachment: null,
            Limit: 10,
            SnippetChars: 1024));
        var betaResults = database.SearchMessages(new(
            Query: "beta",
            AccountFilters: ["yahoo"],
            FromEmail: null,
            FolderRoles: ["inbox"],
            HasAttachment: null,
            Limit: 10,
            SnippetChars: 1024));

        Assert.Equal(2, status.MessageBodyCount);
        Assert.Equal(2, status.MessageSearchDocCount);
        Assert.Single(alphaResults);
        Assert.Single(betaResults);
    }

    [Fact]
    public void PendingBodyTargetsBackfillLegacyAttachmentMessagesOnce()
    {
        using var temp = TempWorkspace.Create();
        var database = new EmailDatabase(temp.Paths);
        var accountId = database.UpsertConfiguredAccount(TestData.Account());
        database.UpsertFolders(accountId, [
            TestData.Folder("Inbox", role: "inbox")
        ]);
        var inbox = database.ReadFolders("yahoo").Single(folder => folder.Path == "Inbox");

        database.UpsertMessageMetadataBatch(accountId, inbox.Id, [
            TestData.Message(
                providerUid: "100",
                providerMessageKey: "emailid:legacy-attachment",
                messageIdHeader: "legacy-attachment@example.com",
                hasAttachments: true),
            TestData.Message(
                providerUid: "101",
                providerMessageKey: "emailid:legacy-no-attachment",
                messageIdHeader: "legacy-no-attachment@example.com",
                hasAttachments: false)
        ], SyncStateJson(matchedCount: 2, selectedCount: 2, fetchedCount: 2), 101);
        var messageIds = ReadInts(temp.Paths.DatabasePath, "SELECT id FROM messages ORDER BY id;");

        database.UpsertMessageBodies([
            new(messageIds[0], "Attachment body", null, "Attachment body", []),
            new(messageIds[1], "Ordinary body", null, "Ordinary body", [])
        ]);
        ExecuteNonQuery(temp.Paths.DatabasePath, "UPDATE messages SET attachments_scanned = 0;");

        var messageOnly = database.GetMessageSearchReadiness(new(
            AccountFilters: ["yahoo"],
            FromEmail: null,
            FolderRoles: ["inbox"],
            HasAttachment: null));
        var withAttachments = database.GetMessageSearchReadiness(new(
            AccountFilters: ["yahoo"],
            FromEmail: null,
            FolderRoles: ["inbox"],
            HasAttachment: null,
            IncludeAttachments: true,
            MimeTypes: ["application/pdf"]));
        var pending = database.ReadPendingBodySyncTargets("yahoo", "Inbox", maxPerFolder: 0);

        var target = Assert.Single(pending);
        Assert.Equal(messageIds[0], target.MessageId);
        Assert.True(messageOnly.SearchReady);
        Assert.False(withAttachments.SearchReady);
        Assert.False(withAttachments.AttachmentSearchIndexComplete);
        Assert.Equal(1, withAttachments.PendingAttachmentMessages);

        database.UpsertMessageBody(new(messageIds[0], "Attachment body", null, "Attachment body", []));

        var completed = database.GetMessageSearchReadiness(new(
            AccountFilters: ["yahoo"],
            FromEmail: null,
            FolderRoles: ["inbox"],
            HasAttachment: null,
            IncludeAttachments: true));

        Assert.Empty(database.ReadPendingBodySyncTargets("yahoo", "Inbox", maxPerFolder: 0));
        Assert.True(completed.SearchReady);
        Assert.True(completed.AttachmentSearchIndexComplete);
        Assert.Equal(0, completed.PendingAttachmentMessages);
    }

    [Fact]
    public void UpsertMessageBodyIndexesAttachmentTextAndManagedAccess()
    {
        using var temp = TempWorkspace.Create();
        var database = new EmailDatabase(temp.Paths);
        var accountId = database.UpsertConfiguredAccount(TestData.Account());
        database.UpsertFolders(accountId, [
            TestData.Folder("Inbox", role: "inbox")
        ]);
        var inbox = database.ReadFolders("yahoo").Single(folder => folder.Path == "Inbox");

        database.UpsertMessageMetadataBatch(accountId, inbox.Id, [
            TestData.Message(
                providerUid: "100",
                providerMessageKey: "emailid:attachment-a",
                messageIdHeader: "attachment-a@example.com",
                subject: "Invoice attached",
                hasAttachments: true)
        ], SyncStateJson(matchedCount: 1, selectedCount: 1, fetchedCount: 1), 100);
        var messageId = ReadInts(temp.Paths.DatabasePath, "SELECT id FROM messages;").Single();
        var stored = new AttachmentObjectStore(temp.Paths).Store(Encoding.UTF8.GetBytes("invoice binary"));

        database.UpsertMessageBody(new(
            MessageId: messageId,
            PlainText: "Please see attached invoice.",
            HtmlText: null,
            NormalizedText: "Please see attached invoice.",
            Recipients: [],
            Attachments: [
                new(
                    SourceKind: "email_part",
                    PartId: "2",
                    Filename: "invoice.txt",
                    DisplayPath: "invoice.txt",
                    ArchiveEntryPath: null,
                    MimeType: "text/plain",
                    SniffedMimeType: "text/plain",
                    SizeBytes: stored.SizeBytes,
                    CompressedSizeBytes: null,
                    UncompressedSizeBytes: stored.SizeBytes,
                    ContentHash: stored.ContentHash,
                    StorageKey: stored.StorageKey,
                    IsContainer: false,
                    NestingDepth: 0,
                    DownloadStatus: "stored",
                    DownloadError: null,
                    ExtractionStatus: "done",
                    ExtractionError: null,
                    ExtractedText: "Invoice total includes VAT and DDV.",
                    OcrText: null,
                    Extractor: "text",
                    Children: [])
            ]));

        var status = database.GetStatus();
        var results = database.SearchMessages(new(
            Query: "DDV",
            AccountFilters: ["yahoo"],
            FromEmail: null,
            FolderRoles: ["inbox"],
            HasAttachment: true,
            Limit: 10,
            SnippetChars: 1024,
            SearchIn: ["attachments"]));
        var result = Assert.Single(results);
        var match = Assert.Single(result.MatchingAttachments);
        var text = database.ReadAttachmentText(match.Attachment.AttachmentId);
        var access = Assert.Single(database.PrepareAttachmentAccess([match.Attachment.AttachmentId]));

        Assert.Equal(1, status.AttachmentCount);
        Assert.Equal(1, status.AttachmentTextCount);
        Assert.Equal(1, status.AttachmentSearchDocCount);
        Assert.Equal(messageId, result.MessageId);
        Assert.Equal("invoice.txt", match.Attachment.DisplayPath);
        Assert.Contains("DDV", match.Snippet, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("VAT", text.CombinedText, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("managed_export_file", access.Kind);
        Assert.True(File.Exists(access.Path));

        database.UpsertMessageBody(new(
            MessageId: messageId,
            PlainText: "Please see attached invoice.",
            HtmlText: null,
            NormalizedText: "Please see attached invoice.",
            Recipients: [],
            Attachments: []));

        var afterCleanup = database.GetStatus();
        var staleResults = database.SearchMessages(new(
            Query: "DDV",
            AccountFilters: ["yahoo"],
            FromEmail: null,
            FolderRoles: ["inbox"],
            HasAttachment: true,
            Limit: 10,
            SnippetChars: 1024,
            SearchIn: ["attachments"],
            AllowPartial: true));

        Assert.Equal(0, afterCleanup.AttachmentCount);
        Assert.Equal(0, afterCleanup.AttachmentTextCount);
        Assert.Equal(0, afterCleanup.AttachmentSearchDocCount);
        Assert.Empty(staleResults);
    }

    [Fact]
    public void AttachmentSpecificFiltersConstrainMessageBranch()
    {
        using var temp = TempWorkspace.Create();
        var database = new EmailDatabase(temp.Paths);
        var accountId = database.UpsertConfiguredAccount(TestData.Account());
        database.UpsertFolders(accountId, [
            TestData.Folder("Inbox", role: "inbox")
        ]);
        var inbox = database.ReadFolders("yahoo").Single(folder => folder.Path == "Inbox");

        database.UpsertMessageMetadataBatch(accountId, inbox.Id, [
            TestData.Message(
                providerUid: "100",
                providerMessageKey: "emailid:body-only-ddv",
                messageIdHeader: "body-only-ddv@example.com",
                subject: "Body only"),
            TestData.Message(
                providerUid: "101",
                providerMessageKey: "emailid:pdf-ddv",
                messageIdHeader: "pdf-ddv@example.com",
                subject: "PDF attachment",
                hasAttachments: true)
        ], SyncStateJson(matchedCount: 2, selectedCount: 2, fetchedCount: 2), 101);
        var messageIds = ReadInts(temp.Paths.DatabasePath, "SELECT id FROM messages ORDER BY id;");
        var stored = new AttachmentObjectStore(temp.Paths).Store(Encoding.UTF8.GetBytes("pdf bytes"));

        database.UpsertMessageBody(new(
            MessageId: messageIds[0],
            PlainText: "This body mentions DDV but has no PDF.",
            HtmlText: null,
            NormalizedText: "This body mentions DDV but has no PDF.",
            Recipients: []));
        database.UpsertMessageBody(new(
            MessageId: messageIds[1],
            PlainText: "See attached statement.",
            HtmlText: null,
            NormalizedText: "See attached statement.",
            Recipients: [],
            Attachments: [
                Attachment(
                    stored,
                    filename: "statement.pdf",
                    mimeType: "application/pdf",
                    extractedText: "PDF text includes DDV.")
            ]));

        var textResults = database.SearchMessages(new(
            Query: "DDV",
            AccountFilters: ["yahoo"],
            FromEmail: null,
            FolderRoles: ["inbox"],
            HasAttachment: null,
            Limit: 10,
            SnippetChars: 1024,
            MimeTypes: ["application/pdf"]));
        var browseResults = database.SearchMessages(new(
            Query: "",
            AccountFilters: ["yahoo"],
            FromEmail: null,
            FolderRoles: ["inbox"],
            HasAttachment: null,
            Limit: 10,
            SnippetChars: 1024,
            MimeTypes: ["application/pdf"]));

        var textResult = Assert.Single(textResults);
        var browseResult = Assert.Single(browseResults);
        Assert.Equal(messageIds[1], textResult.MessageId);
        Assert.Equal(messageIds[1], browseResult.MessageId);
        Assert.Equal("statement.pdf", Assert.Single(textResult.MatchingAttachments).Attachment.DisplayPath);
        Assert.Equal("statement.pdf", Assert.Single(browseResult.MatchingAttachments).Attachment.DisplayPath);
    }

    [Fact]
    public void AttachmentReadinessIsSeparateFromMessageReadiness()
    {
        using var temp = TempWorkspace.Create();
        var database = new EmailDatabase(temp.Paths);
        var accountId = database.UpsertConfiguredAccount(TestData.Account());
        database.UpsertFolders(accountId, [
            TestData.Folder("Inbox", role: "inbox")
        ]);
        var inbox = database.ReadFolders("yahoo").Single(folder => folder.Path == "Inbox");

        database.UpsertMessageMetadataBatch(accountId, inbox.Id, [
            TestData.Message(
                providerUid: "100",
                providerMessageKey: "emailid:pending-attachment",
                messageIdHeader: "pending-attachment@example.com",
                hasAttachments: true)
        ], SyncStateJson(matchedCount: 1, selectedCount: 1, fetchedCount: 1), 100);
        var messageId = ReadInts(temp.Paths.DatabasePath, "SELECT id FROM messages;").Single();
        var stored = new AttachmentObjectStore(temp.Paths).Store(Encoding.UTF8.GetBytes("pending bytes"));
        database.UpsertMessageBody(new(
            MessageId: messageId,
            PlainText: "Body is indexed.",
            HtmlText: null,
            NormalizedText: "Body is indexed.",
            Recipients: [],
            Attachments: [
                Attachment(
                    stored,
                    filename: "pending.pdf",
                    mimeType: "application/pdf",
                    extractedText: null,
                    extractionStatus: "pending")
            ]));

        var messageOnly = database.GetMessageSearchReadiness(new(
            AccountFilters: ["yahoo"],
            FromEmail: null,
            FolderRoles: ["inbox"],
            HasAttachment: null));
        var withAttachments = database.GetMessageSearchReadiness(new(
            AccountFilters: ["yahoo"],
            FromEmail: null,
            FolderRoles: ["inbox"],
            HasAttachment: null,
            IncludeAttachments: true));

        Assert.True(messageOnly.SearchReady);
        Assert.True(messageOnly.MessageSearchIndexComplete);
        Assert.False(withAttachments.SearchReady);
        Assert.False(withAttachments.AttachmentSearchIndexComplete);
        Assert.Equal(1, withAttachments.PendingAttachments);
    }

    [Fact]
    public void SearchMessagesSupportsRecipientDateFiltersAndCursorPaging()
    {
        using var temp = TempWorkspace.Create();
        var database = new EmailDatabase(temp.Paths);
        var accountId = database.UpsertConfiguredAccount(TestData.Account());
        database.UpsertFolders(accountId, [
            TestData.Folder("Inbox", role: "inbox")
        ]);
        var inbox = database.ReadFolders("yahoo").Single(folder => folder.Path == "Inbox");

        database.UpsertMessageMetadataBatch(accountId, inbox.Id, [
            TestData.Message(
                providerUid: "100",
                providerMessageKey: "emailid:invoice-new",
                messageIdHeader: "invoice-new@example.com",
                subject: "Invoice newest",
                dateSent: "2026-06-21T10:00:00.0000000+00:00"),
            TestData.Message(
                providerUid: "101",
                providerMessageKey: "emailid:invoice-old",
                messageIdHeader: "invoice-old@example.com",
                subject: "Invoice older",
                dateSent: "2026-06-20T10:00:00.0000000+00:00"),
            TestData.Message(
                providerUid: "102",
                providerMessageKey: "emailid:invoice-other-recipient",
                messageIdHeader: "invoice-other-recipient@example.com",
                subject: "Invoice other recipient",
                dateSent: "2026-06-21T09:00:00.0000000+00:00")
        ], SyncStateJson(sinceDays: 30, matchedCount: 3, selectedCount: 3, fetchedCount: 3), 102);

        var messageIds = ReadInts(temp.Paths.DatabasePath, "SELECT id FROM messages ORDER BY id;");
        database.UpsertMessageBody(new(
            MessageId: messageIds[0],
            PlainText: "invoice body",
            HtmlText: null,
            NormalizedText: "invoice body",
            Recipients: [new("to", "Billing", "billing@example.com")]));
        database.UpsertMessageBody(new(
            MessageId: messageIds[1],
            PlainText: "invoice body",
            HtmlText: null,
            NormalizedText: "invoice body",
            Recipients: [new("to", "Billing", "billing@example.com")]));
        database.UpsertMessageBody(new(
            MessageId: messageIds[2],
            PlainText: "invoice body",
            HtmlText: null,
            NormalizedText: "invoice body",
            Recipients: [new("to", "Other", "other@example.com")]));

        var dateFrom = EmailSearchDateParser.NormalizeLowerBound("2026-06-20");
        var dateTo = EmailSearchDateParser.NormalizeUpperBound("2026-06-21");
        var firstPage = database.SearchMessages(new(
            Query: "invoice",
            AccountFilters: ["yahoo"],
            FromEmail: null,
            FolderRoles: ["inbox"],
            HasAttachment: null,
            Limit: 1,
            SnippetChars: 1024,
            ToEmail: "billing@example.com",
            DateFrom: dateFrom,
            DateTo: dateTo));
        var secondPage = database.SearchMessages(new(
            Query: "invoice",
            AccountFilters: ["yahoo"],
            FromEmail: null,
            FolderRoles: ["inbox"],
            HasAttachment: null,
            Limit: 5,
            SnippetChars: 1024,
            ToEmail: "billing@example.com",
            DateFrom: dateFrom,
            DateTo: dateTo,
            Cursor: firstPage.Single().Cursor));
        var outsideDate = database.SearchMessages(new(
            Query: "invoice",
            AccountFilters: ["yahoo"],
            FromEmail: null,
            FolderRoles: ["inbox"],
            HasAttachment: null,
            Limit: 5,
            SnippetChars: 1024,
            ToEmail: "billing@example.com",
            DateFrom: EmailSearchDateParser.NormalizeLowerBound("2026-06-22"),
            DateTo: null));

        Assert.Single(firstPage);
        Assert.Single(secondPage);
        Assert.NotEqual(firstPage[0].MessageId, secondPage[0].MessageId);
        Assert.All(firstPage.Concat(secondPage), result => Assert.Contains("Invoice", result.Subject));
        Assert.Empty(outsideDate);
    }

    [Fact]
    public void SearchMessagesSupportsFilterOnlyDateBrowsing()
    {
        using var temp = TempWorkspace.Create();
        var database = new EmailDatabase(temp.Paths);
        var accountId = database.UpsertConfiguredAccount(TestData.Account());
        database.UpsertFolders(accountId, [
            TestData.Folder("Inbox", role: "inbox")
        ]);
        var inbox = database.ReadFolders("yahoo").Single(folder => folder.Path == "Inbox");

        database.UpsertMessageMetadataBatch(accountId, inbox.Id, [
            TestData.Message(
                providerUid: "100",
                providerMessageKey: "emailid:filter-new",
                messageIdHeader: "filter-new@example.com",
                subject: "Newest filtered message",
                dateSent: "2026-06-21T10:00:00.0000000+00:00"),
            TestData.Message(
                providerUid: "101",
                providerMessageKey: "emailid:filter-old",
                messageIdHeader: "filter-old@example.com",
                subject: "Older filtered message",
                dateSent: "2026-06-20T10:00:00.0000000+00:00"),
            TestData.Message(
                providerUid: "102",
                providerMessageKey: "emailid:outside",
                messageIdHeader: "outside@example.com",
                subject: "Outside filtered range",
                dateSent: "2026-05-01T10:00:00.0000000+00:00")
        ], SyncStateJson(sinceDays: 90, matchedCount: 3, selectedCount: 3, fetchedCount: 3), 102);
        var messageIds = ReadInts(temp.Paths.DatabasePath, "SELECT id FROM messages ORDER BY id;");

        foreach (var messageId in messageIds)
        {
            database.UpsertMessageBody(new(
                MessageId: messageId,
                PlainText: "filter-only body",
                HtmlText: null,
                NormalizedText: "filter-only body",
                Recipients: [new("to", "Billing", "billing@example.com")]));
        }

        var dateFrom = EmailSearchDateParser.NormalizeLowerBound("2026-06-01");
        var dateTo = EmailSearchDateParser.NormalizeUpperBound("2026-06-30");
        var firstPage = database.SearchMessages(new(
            Query: "",
            AccountFilters: ["yahoo"],
            FromEmail: null,
            FolderRoles: ["inbox"],
            HasAttachment: null,
            Limit: 1,
            SnippetChars: 1024,
            ToEmail: "billing@example.com",
            DateFrom: dateFrom,
            DateTo: dateTo));
        var secondPage = database.SearchMessages(new(
            Query: "",
            AccountFilters: ["yahoo"],
            FromEmail: null,
            FolderRoles: ["inbox"],
            HasAttachment: null,
            Limit: 5,
            SnippetChars: 1024,
            ToEmail: "billing@example.com",
            DateFrom: dateFrom,
            DateTo: dateTo,
            Cursor: firstPage.Single().Cursor));

        Assert.Equal("Newest filtered message", firstPage.Single().Subject);
        Assert.Equal("Older filtered message", secondPage.Single().Subject);
        Assert.Null(firstPage.Single().Snippet);
        Assert.Equal(0d, firstPage.Single().Score);
    }

    [Fact]
    public void MessageSearchReadinessRequiresCompleteMetadataBodiesAndSearchDocs()
    {
        using var temp = TempWorkspace.Create();
        var database = new EmailDatabase(temp.Paths);
        var accountId = database.UpsertConfiguredAccount(TestData.Account());
        database.UpsertFolders(accountId, [
            TestData.Folder("Inbox", role: "inbox")
        ]);
        var inbox = database.ReadFolders("yahoo").Single(folder => folder.Path == "Inbox");

        database.UpsertMessageMetadataBatch(accountId, inbox.Id, [
            TestData.Message(providerUid: "100", providerMessageKey: "emailid:abc-1", messageIdHeader: "abc-1@example.com"),
            TestData.Message(providerUid: "101", providerMessageKey: "emailid:abc-2", messageIdHeader: "abc-2@example.com")
        ], SyncStateJson(matchedCount: 2, selectedCount: 2, fetchedCount: 2), 101);

        var request = new MessageSearchReadinessRequest(
            AccountFilters: ["yahoo"],
            FromEmail: null,
            FolderRoles: ["inbox"],
            HasAttachment: null);
        var initial = database.GetMessageSearchReadiness(request);
        var messageIds = ReadInts(temp.Paths.DatabasePath, "SELECT id FROM messages ORDER BY id;");

        database.UpsertMessageBody(new(
            MessageId: messageIds[0],
            PlainText: "First indexed body",
            HtmlText: null,
            NormalizedText: "First indexed body",
            Recipients: []));
        var partial = database.GetMessageSearchReadiness(request);

        database.UpsertMessageBody(new(
            MessageId: messageIds[1],
            PlainText: "Second indexed body",
            HtmlText: null,
            NormalizedText: "Second indexed body",
            Recipients: []));
        var ready = database.GetMessageSearchReadiness(request);

        Assert.False(initial.SearchReady);
        Assert.True(initial.MetadataComplete);
        Assert.False(initial.BodiesComplete);
        Assert.False(initial.MessageSearchIndexComplete);
        Assert.Equal(2, initial.PendingMessageBodies);
        Assert.False(partial.SearchReady);
        Assert.Equal(1, partial.PendingMessageBodies);
        Assert.True(ready.SearchReady);
        Assert.Equal(2, ready.MetadataMessages);
        Assert.Equal(2, ready.IndexedMessageBodies);
        Assert.Equal(2, ready.MessageSearchDocs);
        Assert.Equal(2, ready.FtsRows);
        Assert.Equal(0, ready.PendingMessageBodies);
    }

    [Fact]
    public void MessageSearchReadinessRejectsCappedMetadataSync()
    {
        using var temp = TempWorkspace.Create();
        var database = new EmailDatabase(temp.Paths);
        var accountId = database.UpsertConfiguredAccount(TestData.Account());
        database.UpsertFolders(accountId, [
            TestData.Folder("Inbox", role: "inbox")
        ]);
        var inbox = database.ReadFolders("yahoo").Single(folder => folder.Path == "Inbox");

        database.UpsertMessageMetadataBatch(accountId, inbox.Id, [
            TestData.Message(providerUid: "100", providerMessageKey: "emailid:abc-1", messageIdHeader: "abc-1@example.com")
        ], SyncStateJson(maxPerFolder: 1, matchedCount: 2, selectedCount: 1, fetchedCount: 1), 100);
        var messageId = ReadInts(temp.Paths.DatabasePath, "SELECT id FROM messages;").Single();

        database.UpsertMessageBody(new(
            MessageId: messageId,
            PlainText: "Indexed body",
            HtmlText: null,
            NormalizedText: "Indexed body",
            Recipients: []));

        var readiness = database.GetMessageSearchReadiness(new(
            AccountFilters: ["yahoo"],
            FromEmail: null,
            FolderRoles: ["inbox"],
            HasAttachment: null));

        Assert.False(readiness.SearchReady);
        Assert.False(readiness.MetadataComplete);
        Assert.True(readiness.BodiesComplete);
        Assert.True(readiness.MessageSearchIndexComplete);
        Assert.Equal(1, readiness.MetadataMessages);
        Assert.Equal(0, readiness.PendingMessageBodies);
    }

    [Theory]
    [InlineData(60, true)]
    [InlineData(10, false)]
    public void MessageSearchReadinessComparesMetadataWindowToConfiguredHistory(int syncSinceDays, bool expectedReady)
    {
        using var temp = TempWorkspace.Create();
        var database = new EmailDatabase(temp.Paths);
        var accountId = database.UpsertConfiguredAccount(TestData.Account());
        database.UpsertFolders(accountId, [
            TestData.Folder("Inbox", role: "inbox")
        ]);
        var inbox = database.ReadFolders("yahoo").Single(folder => folder.Path == "Inbox");

        database.UpsertMessageMetadataBatch(accountId, inbox.Id, [
            TestData.Message(providerUid: "100", providerMessageKey: "emailid:abc-1", messageIdHeader: "abc-1@example.com")
        ], SyncStateJson(sinceDays: syncSinceDays), 100);
        var messageId = ReadInts(temp.Paths.DatabasePath, "SELECT id FROM messages;").Single();

        database.UpsertMessageBody(new(
            MessageId: messageId,
            PlainText: "Indexed body",
            HtmlText: null,
            NormalizedText: "Indexed body",
            Recipients: []));

        var readiness = database.GetMessageSearchReadiness(new(
            AccountFilters: ["yahoo"],
            FromEmail: null,
            FolderRoles: ["inbox"],
            HasAttachment: null));

        Assert.Equal(expectedReady, readiness.SearchReady);
        Assert.Equal(expectedReady, readiness.MetadataComplete);
        Assert.True(readiness.BodiesComplete);
        Assert.True(readiness.MessageSearchIndexComplete);
    }

    [Fact]
    public void MessageSearchReadinessReportsDateCoverageGaps()
    {
        using var temp = TempWorkspace.Create();
        var database = new EmailDatabase(temp.Paths);
        var accountId = database.UpsertConfiguredAccount(TestData.Account());
        database.UpsertFolders(accountId, [
            TestData.Folder("Inbox", role: "inbox")
        ]);
        var inbox = database.ReadFolders("yahoo").Single(folder => folder.Path == "Inbox");

        database.UpsertMessageMetadataBatch(accountId, inbox.Id, [
            TestData.Message(providerUid: "100", providerMessageKey: "emailid:date-gap", messageIdHeader: "date-gap@example.com")
        ], SyncStateJson(sinceDays: 30), 100);
        var messageId = ReadInts(temp.Paths.DatabasePath, "SELECT id FROM messages;").Single();
        database.UpsertMessageBody(new(
            MessageId: messageId,
            PlainText: "Indexed body",
            HtmlText: null,
            NormalizedText: "Indexed body",
            Recipients: []));

        var readiness = database.GetMessageSearchReadiness(new(
            AccountFilters: ["yahoo"],
            FromEmail: null,
            FolderRoles: ["inbox"],
            HasAttachment: null,
            DateFrom: EmailSearchDateParser.NormalizeLowerBound(DateTimeOffset.UtcNow.AddDays(-60).ToString("yyyy-MM-dd"))));

        Assert.False(readiness.SearchReady);
        Assert.False(readiness.MetadataComplete);
        Assert.NotNull(readiness.CoverageNote);
        Assert.Contains("beyond", readiness.CoverageNote, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void MetadataSyncWindowAutoExpandsFromLastUncappedSuccess()
    {
        using var temp = TempWorkspace.Create();
        var database = new EmailDatabase(temp.Paths);
        var accountId = database.UpsertConfiguredAccount(TestData.Account());
        database.UpsertFolders(accountId, [
            TestData.Folder("Inbox", role: "inbox")
        ]);
        var inbox = database.ReadFolders("yahoo").Single(folder => folder.Path == "Inbox");

        database.MarkFolderSyncSucceeded(accountId, inbox.Id, SyncStateJson(sinceDays: 90), highestUid: 100);
        var oldSuccess = DateTimeOffset.UtcNow.AddDays(-150).ToString("O");
        ExecuteNonQuery(
            temp.Paths.DatabasePath,
            $"""
            UPDATE sync_state
            SET last_success_at = '{oldSuccess}'
            WHERE account_id = {accountId} AND folder_id = {inbox.Id};
            """);

        var expanded = database.PlanMetadataSyncWindow(accountId, [inbox], requestedSinceDays: 90);

        database.MarkFolderSyncSucceeded(accountId, inbox.Id, SyncStateJson(sinceDays: 90, maxPerFolder: 1, matchedCount: 2, selectedCount: 1, fetchedCount: 1), highestUid: 101);
        ExecuteNonQuery(
            temp.Paths.DatabasePath,
            $"""
            UPDATE sync_state
            SET last_success_at = '{oldSuccess}'
            WHERE account_id = {accountId} AND folder_id = {inbox.Id};
            """);

        var capped = database.PlanMetadataSyncWindow(accountId, [inbox], requestedSinceDays: 90);

        Assert.True(expanded.AutoExpandedForGap);
        Assert.True(expanded.EffectiveSinceDays >= 151);
        Assert.Equal(expanded.EffectiveSinceDays, expanded.EffectiveSinceDaysByFolder[inbox.Id]);
        Assert.False(capped.AutoExpandedForGap);
        Assert.Equal(90, capped.EffectiveSinceDays);
    }

    [Fact]
    public void SyncRunsExposeActiveProgressUntilCompleted()
    {
        using var temp = TempWorkspace.Create();
        var database = new EmailDatabase(temp.Paths);
        var accountId = database.UpsertConfiguredAccount(TestData.Account());

        var syncRun = database.StartOrQueueSyncRun(accountId, "yahoo", "Inbox", "syncing_bodies", total: 10);
        var active = database.ReadActiveSyncRun();

        database.UpdateSyncRunProgress(syncRun.Id, syncRun.OwnerId, done: 3, total: 10);
        var progressed = database.ReadActiveSyncRun();

        database.CompleteSyncRun(syncRun.Id, syncRun.OwnerId, succeeded: true, done: 10, total: 10, lastError: null);

        Assert.True(syncRun.Acquired);
        Assert.Equal(syncRun.Id, active.Id);
        Assert.Equal("running", active.Status);
        Assert.Equal("syncing_bodies", active.Phase);
        Assert.Equal(10, active.Total);
        Assert.Equal(3, progressed.Done);
        Assert.Equal(30, progressed.Percent);
        Assert.Null(database.ReadActiveSyncRun());
    }

    [Fact]
    public void SecondSyncRunQueuesUntilActiveLeaseCompletes()
    {
        using var temp = TempWorkspace.Create();
        var database = new EmailDatabase(temp.Paths);
        var accountId = database.UpsertConfiguredAccount(TestData.Account());

        var first = database.StartOrQueueSyncRun(accountId, "yahoo", "Inbox", "syncing_metadata", total: 1);
        var second = database.StartOrQueueSyncRun(accountId, "yahoo", "Sent", "syncing_metadata", total: 1);
        var prematureClaim = database.TryClaimQueuedSyncRun(second.Id, second.OwnerId);

        database.CompleteSyncRun(first.Id, first.OwnerId, succeeded: true, done: 1, total: 1, lastError: null);
        var claimed = database.TryClaimQueuedSyncRun(second.Id, second.OwnerId);

        Assert.True(first.Acquired);
        Assert.False(second.Acquired);
        Assert.Equal("queued", second.Status);
        Assert.False(prematureClaim.Acquired);
        Assert.True(claimed.Acquired);
        Assert.Equal(second.Id, claimed.Id);
        Assert.Equal("running", database.ReadActiveSyncRun().Status);
    }

    [Fact]
    public void OldProgressDoesNotFailRunWhileLeaseIsAlive()
    {
        using var temp = TempWorkspace.Create();
        var database = new EmailDatabase(temp.Paths);
        var accountId = database.UpsertConfiguredAccount(TestData.Account());
        var syncRun = database.StartOrQueueSyncRun(accountId, "yahoo", "Inbox", "syncing_bodies", total: 10);
        var staleTimestamp = DateTimeOffset.UtcNow.AddHours(-7).ToString("O");

        ExecuteNonQuery(
            temp.Paths.DatabasePath,
            $"""
            UPDATE sync_runs
            SET last_progress_at = '{staleTimestamp}'
            WHERE id = '{syncRun.Id}';
            """);

        var active = database.ReadActiveSyncRun();
        var row = ReadNullableNames(
            temp.Paths.DatabasePath,
            $"SELECT status || '|' || COALESCE(last_error, '') FROM sync_runs WHERE id = '{syncRun.Id}';").Single();

        Assert.Equal(syncRun.Id, active.Id);
        Assert.Equal("running", active.Status);
        Assert.Equal("running|", row);
    }

    [Fact]
    public void ExpiredLeaseMarksRunningSyncFailed()
    {
        using var temp = TempWorkspace.Create();
        var database = new EmailDatabase(temp.Paths);
        var accountId = database.UpsertConfiguredAccount(TestData.Account());
        var syncRun = database.StartOrQueueSyncRun(accountId, "yahoo", "Inbox", "syncing_bodies", total: 10);
        var expiredTimestamp = DateTimeOffset.UtcNow.AddMinutes(-5).ToString("O");

        ExecuteNonQuery(
            temp.Paths.DatabasePath,
            $"""
            UPDATE sync_leases
            SET lease_expires_at = '{expiredTimestamp}'
            WHERE sync_run_id = '{syncRun.Id}';
            """);

        var active = database.ReadActiveSyncRun();
        var row = ReadNullableNames(
            temp.Paths.DatabasePath,
            $"SELECT status || '|' || COALESCE(last_error, '') FROM sync_runs WHERE id = '{syncRun.Id}';").Single();

        Assert.Null(active);
        Assert.StartsWith("failed|Sync lease expired", row);
    }

    [Fact]
    public void ExpiredRunningLeaseAllowsQueuedRunToClaim()
    {
        using var temp = TempWorkspace.Create();
        var database = new EmailDatabase(temp.Paths);
        var accountId = database.UpsertConfiguredAccount(TestData.Account());
        var first = database.StartOrQueueSyncRun(accountId, "yahoo", "Inbox", "syncing_bodies", total: 10);
        var second = database.StartOrQueueSyncRun(accountId, "yahoo", "Sent", "syncing_bodies", total: 5);
        var expiredTimestamp = DateTimeOffset.UtcNow.AddMinutes(-5).ToString("O");

        ExecuteNonQuery(
            temp.Paths.DatabasePath,
            $"""
            UPDATE sync_leases
            SET lease_expires_at = '{expiredTimestamp}'
            WHERE sync_run_id = '{first.Id}';
            """);

        var claimed = database.TryClaimQueuedSyncRun(second.Id, second.OwnerId);
        var rows = ReadNullableNames(
            temp.Paths.DatabasePath,
            $"""
            SELECT id || '|' || status || '|' || COALESCE(last_error, '')
            FROM sync_runs
            WHERE id IN ('{first.Id}', '{second.Id}')
            ORDER BY id;
            """);

        Assert.True(claimed.Acquired);
        Assert.Equal(second.Id, claimed.Id);
        Assert.Contains(rows, row => row.StartsWith($"{first.Id}|failed|Sync lease expired"));
        Assert.Contains(rows, row => row == $"{second.Id}|running|");
    }

    [Fact]
    public void ExpiredHeartbeatCannotReviveLease()
    {
        using var temp = TempWorkspace.Create();
        var database = new EmailDatabase(temp.Paths);
        var accountId = database.UpsertConfiguredAccount(TestData.Account());
        var syncRun = database.StartOrQueueSyncRun(accountId, "yahoo", "Inbox", "syncing_bodies", total: 10);
        var expiredTimestamp = DateTimeOffset.UtcNow.AddMinutes(-5).ToString("O");

        ExecuteNonQuery(
            temp.Paths.DatabasePath,
            $"""
            UPDATE sync_leases
            SET lease_expires_at = '{expiredTimestamp}'
            WHERE sync_run_id = '{syncRun.Id}';
            """);

        var heartbeat = database.HeartbeatSyncLease(syncRun.Id, syncRun.OwnerId);
        var ownsLease = database.OwnsActiveSyncLease(syncRun.Id, syncRun.OwnerId);

        Assert.False(heartbeat);
        Assert.False(ownsLease);
    }

    [Fact]
    public void AbandonedQueuedRunDoesNotBlockLaterQueuedRunAfterStaleWindow()
    {
        using var temp = TempWorkspace.Create();
        var database = new EmailDatabase(temp.Paths);
        var accountId = database.UpsertConfiguredAccount(TestData.Account());
        var first = database.StartOrQueueSyncRun(accountId, "yahoo", "Inbox", "syncing_metadata", total: 1);
        var abandoned = database.StartOrQueueSyncRun(accountId, "yahoo", "Archive", "syncing_metadata", total: 1);
        var later = database.StartOrQueueSyncRun(accountId, "yahoo", "Sent", "syncing_metadata", total: 1);
        var staleQueuedTimestamp = DateTimeOffset.UtcNow.AddMinutes(-10).ToString("O");

        ExecuteNonQuery(
            temp.Paths.DatabasePath,
            $"""
            UPDATE sync_runs
            SET last_progress_at = '{staleQueuedTimestamp}'
            WHERE id = '{abandoned.Id}';
            """);

        database.CompleteSyncRun(first.Id, first.OwnerId, succeeded: true, done: 1, total: 1, lastError: null);
        var claimed = database.TryClaimQueuedSyncRun(later.Id, later.OwnerId);
        var abandonedRow = ReadNullableNames(
            temp.Paths.DatabasePath,
            $"SELECT status || '|' || COALESCE(last_error, '') FROM sync_runs WHERE id = '{abandoned.Id}';").Single();

        Assert.True(claimed.Acquired);
        Assert.Equal(later.Id, claimed.Id);
        Assert.StartsWith("failed|Queued sync run became stale", abandonedRow);
    }

    [Fact]
    public void WrongOwnerCannotHeartbeatProgressOrCompleteRun()
    {
        using var temp = TempWorkspace.Create();
        var database = new EmailDatabase(temp.Paths);
        var accountId = database.UpsertConfiguredAccount(TestData.Account());
        var syncRun = database.StartOrQueueSyncRun(accountId, "yahoo", "Inbox", "syncing_bodies", total: 10);
        var wrongOwner = $"{syncRun.OwnerId}-wrong";

        var heartbeat = database.HeartbeatSyncLease(syncRun.Id, wrongOwner);
        var progressed = database.UpdateSyncRunProgress(syncRun.Id, wrongOwner, done: 5, total: 10);
        var completed = database.CompleteSyncRun(syncRun.Id, wrongOwner, succeeded: true, done: 10, total: 10, lastError: null);
        var active = database.ReadActiveSyncRun();

        Assert.False(heartbeat);
        Assert.False(progressed);
        Assert.False(completed);
        Assert.Equal(syncRun.Id, active.Id);
        Assert.Equal("running", active.Status);
        Assert.Equal(0, active.Done);
    }

    [Fact]
    public void StaleOwnerCannotCompleteRunAfterLeaseExpires()
    {
        using var temp = TempWorkspace.Create();
        var database = new EmailDatabase(temp.Paths);
        var accountId = database.UpsertConfiguredAccount(TestData.Account());
        var syncRun = database.StartOrQueueSyncRun(accountId, "yahoo", "Inbox", "syncing_bodies", total: 10);
        var expiredTimestamp = DateTimeOffset.UtcNow.AddMinutes(-5).ToString("O");

        ExecuteNonQuery(
            temp.Paths.DatabasePath,
            $"""
            UPDATE sync_leases
            SET lease_expires_at = '{expiredTimestamp}'
            WHERE sync_run_id = '{syncRun.Id}';
            """);

        database.ReadActiveSyncRun();
        var completed = database.CompleteSyncRun(syncRun.Id, syncRun.OwnerId, succeeded: true, done: 10, total: 10, lastError: null);
        var row = ReadNullableNames(
            temp.Paths.DatabasePath,
            $"SELECT status || '|' || COALESCE(last_error, '') FROM sync_runs WHERE id = '{syncRun.Id}';").Single();

        Assert.False(completed);
        Assert.StartsWith("failed|Sync lease expired", row);
    }

    private static string[] ReadNames(string databasePath, string sql)
    {
        using var connection = OpenReadConnection(databasePath);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        var values = new List<string>();

        while (reader.Read())
            values.Add(reader.GetString(0));

        return [.. values];
    }

    private static string[] ReadNullableNames(string databasePath, string sql)
    {
        using var connection = OpenReadConnection(databasePath);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        var values = new List<string>();

        while (reader.Read())
            values.Add(reader.IsDBNull(0) ? null : reader.GetString(0));

        return [.. values];
    }

    private static long[] ReadLongs(string databasePath, string sql)
    {
        using var connection = OpenReadConnection(databasePath);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        var values = new List<long>();

        while (reader.Read())
            values.Add(reader.GetInt64(0));

        return [.. values];
    }

    private static int[] ReadInts(string databasePath, string sql)
    {
        using var connection = OpenReadConnection(databasePath);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        var values = new List<int>();

        while (reader.Read())
            values.Add(reader.GetInt32(0));

        return [.. values];
    }

    private static void ExecuteNonQuery(string databasePath, string sql)
    {
        using var connection = OpenReadConnection(databasePath);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static SqliteConnection OpenReadConnection(string databasePath)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ToString();
        var connection = new SqliteConnection(connectionString);
        connection.Open();
        return connection;
    }

    private static AttachmentContent Attachment(
        StoredAttachmentObject stored,
        string filename,
        string mimeType,
        string extractedText,
        string extractionStatus = "done") =>
        new(
            SourceKind: "email_part",
            PartId: "2",
            Filename: filename,
            DisplayPath: filename,
            ArchiveEntryPath: null,
            MimeType: mimeType,
            SniffedMimeType: mimeType,
            SizeBytes: stored.SizeBytes,
            CompressedSizeBytes: null,
            UncompressedSizeBytes: stored.SizeBytes,
            ContentHash: stored.ContentHash,
            StorageKey: stored.StorageKey,
            IsContainer: false,
            NestingDepth: 0,
            DownloadStatus: "stored",
            DownloadError: null,
            ExtractionStatus: extractionStatus,
            ExtractionError: null,
            ExtractedText: extractedText,
            OcrText: null,
            Extractor: extractedText is null ? null : "fixture",
            Children: []);

    private static string SyncStateJson(
        int sinceDays = 30,
        int maxPerFolder = 0,
        int matchedCount = 1,
        int selectedCount = 1,
        int fetchedCount = 1,
        int missingCount = 0) =>
        JsonSerializer.Serialize(new Dictionary<string, int>
        {
            ["since_days"] = sinceDays,
            ["max_per_folder"] = maxPerFolder,
            ["matched_count"] = matchedCount,
            ["selected_count"] = selectedCount,
            ["fetched_count"] = fetchedCount,
            ["missing_count"] = missingCount
        });
}
