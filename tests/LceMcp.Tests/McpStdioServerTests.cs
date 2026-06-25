using Microsoft.Data.Sqlite;
using System.Text.Json.Nodes;

namespace LceMcp.Tests;

public sealed class McpStdioServerTests
{
    [Fact]
    public async Task ServeInitializesAndListsEmailTools()
    {
        using var temp = TempWorkspace.Create();
        var output = new StringWriter();
        var error = new StringWriter();
        var input = new StringReader("""
            {"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-11-25","capabilities":{},"clientInfo":{"name":"test-client","version":"1.0.0"}}}
            {"jsonrpc":"2.0","method":"notifications/initialized"}
            {"jsonrpc":"2.0","id":2,"method":"tools/list","params":{}}
            """);
        var server = new McpStdioServer(
            new ConfigStore(temp.Paths),
            new EmailDatabase(temp.Paths),
            input,
            output,
            error);

        var exitCode = await server.RunAsync(CancellationToken.None);
        var lines = OutputLines(output);
        var initialize = JsonNode.Parse(lines[0]).AsObject();
        var tools = JsonNode.Parse(lines[1]).AsObject()["result"]["tools"].AsArray();
        var toolNames = tools.Select(tool => tool["name"].GetValue<string>()).ToArray();
        var setFolderSyncTool = tools.Single(tool => tool["name"].GetValue<string>() == "email_set_folder_sync").AsObject();

        Assert.Equal(0, exitCode);
        Assert.Equal(2, lines.Length);
        Assert.Equal("2025-11-25", initialize["result"]["protocolVersion"].GetValue<string>());
        Assert.Contains("email_list_accounts", toolNames);
        Assert.Contains("email_get_setup_status", toolNames);
        Assert.Contains("email_discover_folders", toolNames);
        Assert.Contains("email_estimate_sync", toolNames);
        Assert.Contains("email_list_folders", toolNames);
        Assert.Contains("email_set_folder_sync", toolNames);
        Assert.Contains("email_search", toolNames);
        Assert.Contains("email_get_message", toolNames);
        Assert.Contains("email_sync_now", toolNames);
        Assert.Contains("email_get_sync_status", toolNames);
        Assert.Contains("does not delete cached messages", setFolderSyncTool["description"].GetValue<string>());
        Assert.DoesNotContain("lcemcp MCP stdio server started", output.ToString());
        Assert.Contains("lcemcp MCP stdio server started", error.ToString());
    }

    [Fact]
    public async Task EmailSetFolderSyncUpdatesCachedFolderAndWritesAuditLog()
    {
        using var temp = TempWorkspace.Create();
        var account = TestData.Account();
        var configStore = new ConfigStore(temp.Paths);
        var config = new AppConfig();
        config.UpsertAccount(account);
        configStore.Save(config);

        var database = new EmailDatabase(temp.Paths);
        var accountId = database.UpsertConfiguredAccount(account);
        database.UpsertFolders(accountId, [
            TestData.Folder("Archive", role: "archive"),
            TestData.Folder("Inbox", role: "inbox")
        ]);

        Assert.True(database.ReadFolders("yahoo").Single(folder => folder.Path == "Archive").SyncEnabled);

        var output = new StringWriter();
        var error = new StringWriter();
        var input = new StringReader("""
            {"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-11-25","capabilities":{},"clientInfo":{"name":"test-client","version":"1.0.0"}}}
            {"jsonrpc":"2.0","method":"notifications/initialized"}
            {"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"email_set_folder_sync","arguments":{"account":"yahoo","folder":"Archive","enabled":false}}}
            """);
        var server = new McpStdioServer(configStore, database, input, output, error);

        await server.RunAsync(CancellationToken.None);

        var lines = OutputLines(output);
        var structured = JsonNode.Parse(lines[1]).AsObject()["result"]["structuredContent"].AsObject();
        var updatedFolder = structured["folder"].AsObject();
        var storedFolder = database.ReadFolders("yahoo").Single(folder => folder.Path == "Archive");
        var auditRows = ReadStrings(
            temp.Paths.DatabasePath,
            "SELECT client_name || '|' || tool_name || '|' || action_type || '|' || result_summary FROM audit_log ORDER BY id;");

        Assert.Equal("updated", structured["status"].GetValue<string>());
        Assert.True(structured["updated"].GetValue<bool>());
        Assert.False(structured["sync_enabled"].GetValue<bool>());
        Assert.False(updatedFolder["sync_enabled"].GetValue<bool>());
        Assert.False(storedFolder.SyncEnabled);
        Assert.Contains("Existing cached mail was not deleted", structured["message"].GetValue<string>());
        Assert.Equal(["test-client|email_set_folder_sync|config|status=updated folder=Archive sync_enabled=false"], auditRows);
    }

    [Fact]
    public async Task EmailSearchAndGetMessageUseReadyLocalIndexAndAuditResults()
    {
        using var temp = TempWorkspace.Create();
        var account = TestData.Account();
        var configStore = new ConfigStore(temp.Paths);
        var config = new AppConfig();
        config.UpsertAccount(account);
        configStore.Save(config);

        var database = new EmailDatabase(temp.Paths);
        var accountId = database.UpsertConfiguredAccount(account);
        database.UpsertFolders(accountId, [
            TestData.Folder("Inbox", role: "inbox")
        ]);
        var inbox = database.ReadFolders("yahoo").Single(folder => folder.Path == "Inbox");

        database.UpsertMessageMetadataBatch(accountId, inbox.Id, [
            TestData.Message(providerUid: "100", providerMessageKey: "emailid:ready-search", messageIdHeader: "ready-search@example.com", subject: "Refund update")
        ], SyncStateJson(matchedCount: 1, selectedCount: 1, fetchedCount: 1), 100);
        var messageId = ReadInts(temp.Paths.DatabasePath, "SELECT id FROM messages;").Single();

        database.UpsertMessageBody(new(
            MessageId: messageId,
            PlainText: "Hello, the refund processed yesterday.",
            HtmlText: null,
            NormalizedText: "Hello, the refund processed yesterday.",
            Recipients: [
                new("to", "Customer", "customer@example.com")
            ]));

        var output = new StringWriter();
        var error = new StringWriter();
        var inputText = """
            {"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-11-25","capabilities":{},"clientInfo":{"name":"test-client","version":"1.0.0"}}}
            {"jsonrpc":"2.0","method":"notifications/initialized"}
            {"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"email_search","arguments":{"query":"refund","accounts":["yahoo"],"folder_roles":["inbox"],"limit":5}}}
            {"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"email_get_message","arguments":{"message_id":__MESSAGE_ID__,"include_body":true,"max_body_chars":2000}}}
            """.Replace("__MESSAGE_ID__", messageId.ToString());
        var input = new StringReader(inputText);
        var server = new McpStdioServer(configStore, database, input, output, error);

        await server.RunAsync(CancellationToken.None);

        var lines = OutputLines(output);
        var search = JsonNode.Parse(lines[1]).AsObject()["result"]["structuredContent"].AsObject();
        var searchResult = search["results"].AsArray()[0].AsObject();
        var message = JsonNode.Parse(lines[2]).AsObject()["result"]["structuredContent"]["message"].AsObject();
        var auditRows = ReadStrings(
            temp.Paths.DatabasePath,
            "SELECT tool_name || '|' || result_summary || '|' || COALESCE(affected_message_ids, '') FROM audit_log ORDER BY id;");

        Assert.Equal("ready", search["status"].GetValue<string>());
        Assert.True(search["search_ready"].GetValue<bool>());
        Assert.Equal(messageId, searchResult["message_id"].GetValue<int>());
        Assert.Contains("refund", searchResult["message_snippets"].AsArray()[0]["text"].GetValue<string>(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal(messageId, message["message_id"].GetValue<int>());
        Assert.Contains("refund processed", message["body_text"].GetValue<string>(), StringComparison.OrdinalIgnoreCase);
        Assert.Contains($"email_search|status=ready results=1 has_more=false|{messageId}", auditRows);
        Assert.Contains($"email_get_message|status=ok message=1|{messageId}", auditRows);
    }

    [Fact]
    public async Task EmailSearchAllowsDateOnlyRequestWithoutQuery()
    {
        using var temp = TempWorkspace.Create();
        var account = TestData.Account();
        var configStore = new ConfigStore(temp.Paths);
        var config = new AppConfig();
        config.UpsertAccount(account);
        configStore.Save(config);

        var database = new EmailDatabase(temp.Paths);
        var accountId = database.UpsertConfiguredAccount(account);
        database.UpsertFolders(accountId, [
            TestData.Folder("Inbox", role: "inbox")
        ]);
        var inbox = database.ReadFolders("yahoo").Single(folder => folder.Path == "Inbox");

        database.UpsertMessageMetadataBatch(accountId, inbox.Id, [
            TestData.Message(
                providerUid: "100",
                providerMessageKey: "emailid:date-only",
                messageIdHeader: "date-only@example.com",
                subject: "Date only browse",
                dateSent: "2026-06-19T10:00:00.0000000+00:00")
        ], SyncStateJson(sinceDays: 30, matchedCount: 1, selectedCount: 1, fetchedCount: 1), 100);
        var messageId = ReadInts(temp.Paths.DatabasePath, "SELECT id FROM messages;").Single();
        database.UpsertMessageBody(new(
            MessageId: messageId,
            PlainText: "This body should not require an FTS query.",
            HtmlText: null,
            NormalizedText: "This body should not require an FTS query.",
            Recipients: []));

        var output = new StringWriter();
        var error = new StringWriter();
        var input = new StringReader("""
            {"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-11-25","capabilities":{},"clientInfo":{"name":"test-client","version":"1.0.0"}}}
            {"jsonrpc":"2.0","method":"notifications/initialized"}
            {"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"email_search","arguments":{"accounts":["yahoo"],"date_from":"2026-06-19","date_to":"2026-06-19","limit":5}}}
            """);
        var server = new McpStdioServer(configStore, database, input, output, error);

        await server.RunAsync(CancellationToken.None);

        var lines = OutputLines(output);
        var search = JsonNode.Parse(lines[1]).AsObject()["result"]["structuredContent"].AsObject();
        var result = search["results"].AsArray()[0].AsObject();

        Assert.Equal("ready", search["status"].GetValue<string>());
        Assert.Equal(messageId, result["message_id"].GetValue<int>());
        Assert.Equal("Date only browse", result["subject"].GetValue<string>());
        Assert.Empty(result["message_snippets"].AsArray());
        Assert.Equal(0d, result["score"].GetValue<double>());
    }

    [Fact]
    public async Task EmailSearchReportsScopedFreshnessForReadyLocalIndex()
    {
        using var temp = TempWorkspace.Create();
        var account = TestData.Account();
        var configStore = new ConfigStore(temp.Paths);
        var config = new AppConfig();
        config.UpsertAccount(account);
        configStore.Save(config);

        var database = new EmailDatabase(temp.Paths);
        var accountId = database.UpsertConfiguredAccount(account);
        database.UpsertFolders(accountId, [
            TestData.Folder("Inbox", role: "inbox"),
            TestData.Folder("Sent", role: "sent")
        ]);
        var folders = database.ReadFolders("yahoo");
        var inbox = folders.Single(folder => folder.Path == "Inbox");
        var sent = folders.Single(folder => folder.Path == "Sent");
        var now = DateTimeOffset.UtcNow;
        var oldestSync = now.AddHours(-2).ToString("O");
        var newestSync = now.AddHours(-1).ToString("O");
        var dateFrom = now.AddDays(-1).ToString("O");
        var dateTo = now.ToString("O");

        database.UpsertMessageMetadataBatch(accountId, inbox.Id, [
            TestData.Message(
                providerUid: "100",
                providerMessageKey: "emailid:freshness-inbox",
                messageIdHeader: "freshness-inbox@example.com",
                subject: "Freshness inbox",
                dateSent: now.AddMinutes(-30).ToString("O"))
        ], SyncStateJson(matchedCount: 1, selectedCount: 1, fetchedCount: 1), 100);
        database.UpsertMessageMetadataBatch(accountId, sent.Id, [
            TestData.Message(
                providerUid: "200",
                providerMessageKey: "emailid:freshness-sent",
                messageIdHeader: "freshness-sent@example.com",
                subject: "Freshness sent",
                dateSent: now.AddMinutes(-20).ToString("O"))
        ], SyncStateJson(matchedCount: 1, selectedCount: 1, fetchedCount: 1), 200);

        var messageIds = ReadInts(temp.Paths.DatabasePath, "SELECT id FROM messages ORDER BY id;");
        foreach (var messageId in messageIds)
        {
            database.UpsertMessageBody(new(
                MessageId: messageId,
                PlainText: "freshness marker",
                HtmlText: null,
                NormalizedText: "freshness marker",
                Recipients: []));
        }

        ExecuteNonQuery(
            temp.Paths.DatabasePath,
            """
            UPDATE sync_state
            SET last_success_at = CASE
                    WHEN folder_id = $inboxId THEN $oldestSync
                    WHEN folder_id = $sentId THEN $newestSync
                    ELSE last_success_at
                END
            WHERE folder_id IN ($inboxId, $sentId);
            """,
            command =>
            {
                command.Parameters.AddWithValue("$inboxId", inbox.Id);
                command.Parameters.AddWithValue("$sentId", sent.Id);
                command.Parameters.AddWithValue("$oldestSync", oldestSync);
                command.Parameters.AddWithValue("$newestSync", newestSync);
            });

        var output = new StringWriter();
        var error = new StringWriter();
        var inputText = """
            {"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-11-25","capabilities":{},"clientInfo":{"name":"test-client","version":"1.0.0"}}}
            {"jsonrpc":"2.0","method":"notifications/initialized"}
            {"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"email_search","arguments":{"query":"freshness","accounts":["yahoo"],"date_from":"__DATE_FROM__","date_to":"__DATE_TO__","limit":5}}}
            """
            .Replace("__DATE_FROM__", dateFrom)
            .Replace("__DATE_TO__", dateTo);
        var input = new StringReader(inputText);
        var server = new McpStdioServer(configStore, database, input, output, error);

        await server.RunAsync(CancellationToken.None);

        var lines = OutputLines(output);
        var search = JsonNode.Parse(lines[1]).AsObject()["result"]["structuredContent"].AsObject();
        var freshness = search["freshness"].AsObject();

        Assert.Equal("ready", search["status"].GetValue<string>());
        Assert.True(search["search_ready"].GetValue<bool>());
        Assert.Equal("local_cache", freshness["source"].GetValue<string>());
        Assert.Equal(oldestSync, freshness["search_scope_as_of"].GetValue<string>());
        Assert.Equal(newestSync, freshness["last_sync_performed_at"].GetValue<string>());
        Assert.Equal(oldestSync, freshness["oldest_scoped_sync_at"].GetValue<string>());
        Assert.Equal(newestSync, freshness["newest_scoped_sync_at"].GetValue<string>());
        Assert.True(freshness["cache_age_seconds"].GetValue<int>() >= 0);
        Assert.Equal(dateFrom, freshness["requested_date_from"].GetValue<string>());
        Assert.Equal(dateTo, freshness["requested_date_to"].GetValue<string>());
        Assert.True(freshness["requested_range_extends_beyond_cache"].GetValue<bool>());
    }

    [Fact]
    public async Task EmailGetSetupStatusReportsLocalPrerequisitesOnly()
    {
        using var temp = TempWorkspace.Create();
        var account = TestData.Account(id: "missing-credential", email: "missing-credential@example.com");
        var configStore = new ConfigStore(temp.Paths);
        var config = new AppConfig();
        config.UpsertAccount(account);
        configStore.Save(config);

        var output = new StringWriter();
        var error = new StringWriter();
        var input = new StringReader("""
            {"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-11-25","capabilities":{},"clientInfo":{"name":"test-client","version":"1.0.0"}}}
            {"jsonrpc":"2.0","method":"notifications/initialized"}
            {"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"email_get_setup_status","arguments":{"accounts":["missing-credential"]}}}
            """);
        var server = new McpStdioServer(configStore, new EmailDatabase(temp.Paths), input, output, error);

        await server.RunAsync(CancellationToken.None);

        var lines = OutputLines(output);
        var structured = JsonNode.Parse(lines[1]).AsObject()["result"]["structuredContent"].AsObject();
        var statusAccount = structured["accounts"].AsArray()[0].AsObject();

        Assert.Equal("needs_attention", structured["status"].GetValue<string>());
        Assert.Equal("credential_missing", statusAccount["setup_status"].GetValue<string>());
        Assert.Equal("missing", statusAccount["credential_status"].GetValue<string>());
        Assert.False(statusAccount["folders_cached"].GetValue<bool>());
        Assert.Equal(30, statusAccount["default_history_days"].GetValue<int>());
    }

    [Fact]
    public async Task EmailGetSyncStatusReturnsReadinessAndWritesAuditLog()
    {
        using var temp = TempWorkspace.Create();
        var account = TestData.Account();
        var configStore = new ConfigStore(temp.Paths);
        var config = new AppConfig();
        config.UpsertAccount(account);
        configStore.Save(config);

        var database = new EmailDatabase(temp.Paths);
        var accountId = database.UpsertConfiguredAccount(account);
        database.UpsertFolders(accountId, [
            TestData.Folder("Inbox", role: "inbox")
        ]);

        var output = new StringWriter();
        var error = new StringWriter();
        var input = new StringReader("""
            {"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-11-25","capabilities":{},"clientInfo":{"name":"test-client","version":"1.0.0"}}}
            {"jsonrpc":"2.0","method":"notifications/initialized"}
            {"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"email_get_sync_status","arguments":{"accounts":["yahoo"],"include_folders":true}}}
            """);
        var server = new McpStdioServer(configStore, database, input, output, error);

        await server.RunAsync(CancellationToken.None);

        var lines = OutputLines(output);
        var call = JsonNode.Parse(lines[1]).AsObject();
        var structured = call["result"]["structuredContent"].AsObject();
        var polling = structured["polling"].AsObject();
        var statusAccount = structured["accounts"].AsArray()[0].AsObject();
        var readiness = statusAccount["readiness"].AsObject();
        var folders = statusAccount["folders"].AsArray();
        var auditRows = ReadStrings(
            temp.Paths.DatabasePath,
            "SELECT client_name || '|' || tool_name || '|' || action_type || '|' || result_summary FROM audit_log ORDER BY id;");

        Assert.False(statusAccount["search_ready"].GetValue<bool>());
        Assert.Equal(15, polling["recommended_interval_seconds"].GetValue<int>());
        Assert.False(readiness["metadata_complete"].GetValue<bool>());
        Assert.Equal(1, readiness["scope_folder_count"].GetValue<int>());
        Assert.Equal("Inbox", folders[0]["folder"].GetValue<string>());
        Assert.Equal(["test-client|email_get_sync_status|read|accounts=1, search_ready=0"], auditRows);
    }

    private static string[] OutputLines(StringWriter output) =>
        output.ToString()
            .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries)
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)
            .ToArray();

    private static string[] ReadStrings(string databasePath, string sql)
    {
        using var connection = OpenConnection(databasePath);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        var values = new List<string>();

        while (reader.Read())
            values.Add(reader.GetString(0));

        return [.. values];
    }

    private static int[] ReadInts(string databasePath, string sql)
    {
        using var connection = OpenConnection(databasePath);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        using var reader = command.ExecuteReader();
        var values = new List<int>();

        while (reader.Read())
            values.Add(reader.GetInt32(0));

        return [.. values];
    }

    private static void ExecuteNonQuery(
        string databasePath,
        string sql,
        Action<SqliteCommand> bind)
    {
        using var connection = OpenConnection(databasePath);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        bind(command);
        command.ExecuteNonQuery();
    }

    private static string SyncStateJson(
        int sinceDays = 30,
        int maxPerFolder = 0,
        int matchedCount = 1,
        int selectedCount = 1,
        int fetchedCount = 1,
        int missingCount = 0) =>
        System.Text.Json.JsonSerializer.Serialize(new Dictionary<string, int>
        {
            ["since_days"] = sinceDays,
            ["max_per_folder"] = maxPerFolder,
            ["matched_count"] = matchedCount,
            ["selected_count"] = selectedCount,
            ["fetched_count"] = fetchedCount,
            ["missing_count"] = missingCount
        });

    private static SqliteConnection OpenConnection(string databasePath)
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
}
