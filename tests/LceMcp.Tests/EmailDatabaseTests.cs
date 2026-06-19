using Microsoft.Data.Sqlite;

namespace LceMcp.Tests;

public sealed class EmailDatabaseTests
{
    [Fact]
    public void GetStatusInitializesPrototypeSchemaV2()
    {
        using var temp = TempWorkspace.Create();
        var database = new EmailDatabase(temp.Paths);

        var status = database.GetStatus();

        Assert.Equal(DatabaseInitializationKind.Created, status.InitializationKind);
        Assert.Equal("prototype-reset", status.SchemaMode);
        Assert.True(status.MigrationsLocked);
        Assert.Equal(2, status.SchemaVersion);
        Assert.Equal(2, status.TargetSchemaVersion);
        Assert.Equal(0, status.AccountCount);
        Assert.Equal(0, status.FolderCount);
        Assert.Equal(0, status.MessageCount);
        Assert.Equal(0, status.MessageLocationCount);
        Assert.Null(status.LastSyncState);
        Assert.True(File.Exists(temp.Paths.DatabasePath));
        Assert.True(Directory.Exists(temp.Paths.AttachmentsDirectory));
        Assert.True(Directory.Exists(temp.Paths.LogsDirectory));

        Assert.Equal(
            [
                "accounts",
                "audit_log",
                "folders",
                "message_locations",
                "messages",
                "schema_migrations",
                "sync_state"
            ],
            ReadNames(
                temp.Paths.DatabasePath,
                "SELECT name FROM sqlite_master WHERE type = 'table' AND name NOT LIKE 'sqlite_%' ORDER BY name;"));
    }

    [Fact]
    public void GetStatusRecreatesNonPrototypeDatabaseWhileMigrationsAreLocked()
    {
        using var temp = TempWorkspace.Create();
        Directory.CreateDirectory(temp.Paths.ConfigDirectory);
        File.WriteAllText(temp.Paths.DatabasePath, "not sqlite");
        var database = new EmailDatabase(temp.Paths);

        var status = database.GetStatus();

        Assert.Equal(DatabaseInitializationKind.RecreatedPrototype, status.InitializationKind);
        Assert.Equal(2, status.SchemaVersion);
        Assert.Equal(0, status.MessageCount);
        Assert.Equal(["prototype_schema_metadata_sync"], ReadNames(
            temp.Paths.DatabasePath,
            "SELECT name FROM schema_migrations ORDER BY version;"));
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

    private static SqliteConnection OpenReadConnection(string databasePath)
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ToString();
        var connection = new SqliteConnection(connectionString);
        connection.Open();
        return connection;
    }
}
