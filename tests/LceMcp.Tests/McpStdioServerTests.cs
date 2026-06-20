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

        Assert.Equal(0, exitCode);
        Assert.Equal(2, lines.Length);
        Assert.Equal("2025-11-25", initialize["result"]["protocolVersion"].GetValue<string>());
        Assert.Contains("email_list_accounts", toolNames);
        Assert.Contains("email_list_folders", toolNames);
        Assert.Contains("email_search", toolNames);
        Assert.Contains("email_get_message", toolNames);
        Assert.Contains("email_sync_now", toolNames);
        Assert.Contains("email_get_sync_status", toolNames);
        Assert.DoesNotContain("lcemcp MCP stdio server started", output.ToString());
        Assert.Contains("lcemcp MCP stdio server started", error.ToString());
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
        var statusAccount = structured["accounts"].AsArray()[0].AsObject();
        var readiness = statusAccount["readiness"].AsObject();
        var folders = statusAccount["folders"].AsArray();
        var auditRows = ReadStrings(
            temp.Paths.DatabasePath,
            "SELECT client_name || '|' || tool_name || '|' || action_type || '|' || result_summary FROM audit_log ORDER BY id;");

        Assert.False(statusAccount["search_ready"].GetValue<bool>());
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
