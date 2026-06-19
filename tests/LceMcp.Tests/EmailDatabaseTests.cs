using Microsoft.Data.Sqlite;
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
        Assert.Equal(4, status.SchemaVersion);
        Assert.Equal(4, status.TargetSchemaVersion);
        Assert.Equal(0, status.AccountCount);
        Assert.Equal(0, status.FolderCount);
        Assert.Equal(0, status.MessageCount);
        Assert.Equal(0, status.MessageLocationCount);
        Assert.Equal(0, status.MessageBodyCount);
        Assert.Equal(0, status.MessageSearchDocCount);
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
        Assert.Equal(4, status.SchemaVersion);
        Assert.Equal(["initial_metadata_cache", "message_bodies_and_search", "sync_runs_and_search_readiness", "sync_queue_and_leases"], ReadNames(
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
        Assert.Equal(4, status.SchemaVersion);
        Assert.Equal(4, status.TargetSchemaVersion);
        Assert.Equal(["initial_metadata_cache", "message_bodies_and_search", "sync_runs_and_search_readiness", "sync_queue_and_leases"], ReadNames(
            temp.Paths.DatabasePath,
            "SELECT name FROM schema_migrations ORDER BY version;"));
        Assert.Contains("message_search_docs", ReadNames(
            temp.Paths.DatabasePath,
            "SELECT name FROM sqlite_master WHERE type = 'table' ORDER BY name;"));
        Assert.Contains("sync_runs", ReadNames(
            temp.Paths.DatabasePath,
            "SELECT name FROM sqlite_master WHERE type = 'table' ORDER BY name;"));
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

        Assert.Equal(firstAccountId, secondAccountId);
        Assert.Equal(2, persisted);
        Assert.Equal(1, status.AccountCount);
        Assert.Equal(2, status.FolderCount);
        Assert.Equal(@"\Inbox \HasNoChildren", inbox.Attributes);
        Assert.Equal(11, inbox.MessageCount);
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
