using Microsoft.Data.Sqlite;
using System.Text;
using System.Text.Json.Nodes;

namespace LceMcp.Tests;

[Collection("Process-wide attachment extraction gate")]
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
        Assert.Contains("email_list_ocr_languages", toolNames);
        Assert.Contains("email_set_ocr_config", toolNames);
        Assert.Contains("email_discover_folders", toolNames);
        Assert.Contains("email_estimate_sync", toolNames);
        Assert.Contains("email_list_folders", toolNames);
        Assert.Contains("email_set_folder_sync", toolNames);
        Assert.Contains("email_search", toolNames);
        Assert.Contains("email_get_message", toolNames);
        Assert.Contains("email_get_attachment_text", toolNames);
        Assert.Contains("email_prepare_attachment_access", toolNames);
        Assert.Contains("email_list_attachment_extraction_failures", toolNames);
        Assert.Contains("email_retry_attachment_extraction", toolNames);
        Assert.Contains("email_sync_now", toolNames);
        Assert.Contains("email_get_sync_status", toolNames);
        Assert.Contains("does not delete cached messages", setFolderSyncTool["description"].GetValue<string>());
        Assert.DoesNotContain("lcemcp MCP stdio server started", output.ToString());
        Assert.Contains("lcemcp MCP stdio server started", error.ToString());
    }

    [Fact]
    public async Task EmailOcrConfigToolsListCatalogAndPersistLanguageUpdates()
    {
        using var temp = TempWorkspace.Create();
        var configStore = new ConfigStore(temp.Paths);
        configStore.Save(new AppConfig());
        var database = new EmailDatabase(temp.Paths);
        var output = new StringWriter();
        var error = new StringWriter();
        var input = new StringReader("""
            {"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-11-25","capabilities":{},"clientInfo":{"name":"test-client","version":"1.0.0"}}}
            {"jsonrpc":"2.0","method":"notifications/initialized"}
            {"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"email_list_ocr_languages","arguments":{}}}
            {"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"email_set_ocr_config","arguments":{"enabled":true,"languages":["ENG","srp_latn"],"fallback_script":"Cyrillic"}}}
            """);
        var server = new McpStdioServer(configStore, database, input, output, error);

        await server.RunAsync(CancellationToken.None);

        var lines = OutputLines(output);
        var catalog = JsonNode.Parse(lines[1]).AsObject()["result"]["structuredContent"].AsObject();
        var languageCodes = catalog["language_codes"].AsArray().Select(value => value.GetValue<string>()).ToArray();
        var scriptModels = catalog["script_models"].AsArray().Select(value => value.GetValue<string>()).ToArray();
        var update = JsonNode.Parse(lines[2]).AsObject()["result"]["structuredContent"].AsObject();
        var current = update["current_config"].AsObject();
        var saved = configStore.Load().Ocr;
        var auditRows = ReadStrings(
            temp.Paths.DatabasePath,
            "SELECT tool_name || '|' || action_type || '|' || result_summary FROM audit_log ORDER BY id;");

        Assert.Contains("eng", languageCodes);
        Assert.Contains("srp_latn", languageCodes);
        Assert.Contains("Latin", scriptModels);
        Assert.Contains("Cyrillic", scriptModels);
        Assert.Equal("updated", update["status"].GetValue<string>());
        Assert.True(update["updated"].GetValue<bool>());
        Assert.False(update["restart_required"].GetValue<bool>());
        Assert.True(current["enabled"].GetValue<bool>());
        Assert.Equal("configured_languages", current["mode"].GetValue<string>());
        Assert.Equal(["eng", "srp_latn"], current["languages"].AsArray().Select(value => value.GetValue<string>()).ToArray());
        Assert.True(current["auto_download_language_packs"].GetValue<bool>());
        Assert.True(saved.Enabled);
        Assert.True(saved.AutoDownloadLanguagePacks);
        Assert.Equal("Cyrillic", saved.FallbackScript);
        Assert.Equal(["eng", "srp_latn"], saved.Languages);
        Assert.Contains(auditRows, row => row.StartsWith("email_list_ocr_languages|read|languages=", StringComparison.Ordinal));
        Assert.Contains("email_set_ocr_config|config|status=updated enabled=true mode=configured_languages", auditRows);
    }

    [Fact]
    public async Task EmailSetOcrConfigSupportsAutoScriptModeWithOfflineWarnings()
    {
        using var temp = TempWorkspace.Create();
        var configStore = new ConfigStore(temp.Paths);
        configStore.Save(new AppConfig());
        var database = new EmailDatabase(temp.Paths);
        var output = new StringWriter();
        var error = new StringWriter();
        var input = new StringReader("""
            {"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-11-25","capabilities":{},"clientInfo":{"name":"test-client","version":"1.0.0"}}}
            {"jsonrpc":"2.0","method":"notifications/initialized"}
            {"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"email_set_ocr_config","arguments":{"enabled":true,"languages":[],"fallback_script":"Cyrillic","auto_download_language_packs":false}}}
            """);
        var server = new McpStdioServer(configStore, database, input, output, error);

        await server.RunAsync(CancellationToken.None);

        var lines = OutputLines(output);
        var structured = JsonNode.Parse(lines[1]).AsObject()["result"]["structuredContent"].AsObject();
        var current = structured["current_config"].AsObject();
        var expectedModels = current["expected_models"].AsArray().Select(value => value.GetValue<string>()).ToArray();
        var missingModels = current["missing_cached_models"].AsArray().Select(value => value.GetValue<string>()).ToArray();
        var warnings = structured["warnings"].AsArray().Select(value => value.GetValue<string>()).ToArray();
        var saved = configStore.Load().Ocr;

        Assert.Equal("auto_script", current["mode"].GetValue<string>());
        Assert.False(current["auto_download_language_packs"].GetValue<bool>());
        Assert.False(current["offline_cache_ready"].GetValue<bool>());
        Assert.Equal(["osd", "Cyrillic"], expectedModels);
        Assert.Equal(["osd", "Cyrillic"], missingModels);
        Assert.Contains(warnings, warning => warning.Contains("Automatic OCR model downloads are disabled", StringComparison.Ordinal));
        Assert.True(saved.Enabled);
        Assert.False(saved.AutoDownloadLanguagePacks);
        Assert.Empty(saved.Languages);
    }

    [Fact]
    public async Task EmailSetOcrConfigRejectsUnknownLanguageWithoutSaving()
    {
        using var temp = TempWorkspace.Create();
        var configStore = new ConfigStore(temp.Paths);
        configStore.Save(new AppConfig());
        var database = new EmailDatabase(temp.Paths);
        var output = new StringWriter();
        var error = new StringWriter();
        var input = new StringReader("""
            {"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-11-25","capabilities":{},"clientInfo":{"name":"test-client","version":"1.0.0"}}}
            {"jsonrpc":"2.0","method":"notifications/initialized"}
            {"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"email_set_ocr_config","arguments":{"enabled":true,"languages":["not_a_real_tesseract_code"]}}}
            """);
        var server = new McpStdioServer(configStore, database, input, output, error);

        await server.RunAsync(CancellationToken.None);

        var lines = OutputLines(output);
        var result = JsonNode.Parse(lines[1]).AsObject()["result"].AsObject();
        var structured = result["structuredContent"].AsObject();
        var saved = configStore.Load().Ocr;
        var auditRows = ReadStrings(
            temp.Paths.DatabasePath,
            "SELECT tool_name || '|' || action_type || '|' || result_summary FROM audit_log ORDER BY id;");

        Assert.True(result["isError"].GetValue<bool>());
        Assert.Equal("failed", structured["status"].GetValue<string>());
        Assert.Equal("unsupported_ocr_language", structured["error_code"].GetValue<string>());
        Assert.False(saved.Enabled);
        Assert.Empty(saved.Languages);
        Assert.Contains("email_set_ocr_config|config|status=failed error_code=unsupported_ocr_language", auditRows);
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
    public async Task EmailAttachmentToolsReadTextPrepareAccessAndWriteAuditLog()
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
                providerMessageKey: "emailid:mcp-attachment",
                messageIdHeader: "mcp-attachment@example.com",
                subject: "Attachment tools",
                hasAttachments: true)
        ], SyncStateJson(matchedCount: 1, selectedCount: 1, fetchedCount: 1), 100);
        var messageId = ReadInts(temp.Paths.DatabasePath, "SELECT id FROM messages;").Single();
        var stored = new AttachmentObjectStore(temp.Paths).Store(Encoding.UTF8.GetBytes("statement bytes"));

        database.UpsertMessageBody(new(
            MessageId: messageId,
            PlainText: "Statement attached.",
            HtmlText: null,
            NormalizedText: "Statement attached.",
            Recipients: [],
            Attachments: [
                new(
                    SourceKind: "email_part",
                    PartId: "2",
                    Filename: "statement.txt",
                    DisplayPath: "statement.txt",
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
                    ExtractedText: "Statement text mentions DDV.",
                    OcrText: null,
                    Extractor: "text",
                    Children: [])
            ]));
        var attachmentId = ReadInts(temp.Paths.DatabasePath, "SELECT id FROM attachments;").Single();

        var output = new StringWriter();
        var error = new StringWriter();
        var inputText = """
            {"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-11-25","capabilities":{},"clientInfo":{"name":"test-client","version":"1.0.0"}}}
            {"jsonrpc":"2.0","method":"notifications/initialized"}
            {"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"email_search","arguments":{"query":"DDV","accounts":["yahoo"],"search_in":["attachments"],"mime_types":["text/plain"],"limit":5}}}
            {"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"email_get_attachment_text","arguments":{"attachment_id":__ATTACHMENT_ID__,"max_chars":500}}}
            {"jsonrpc":"2.0","id":4,"method":"tools/call","params":{"name":"email_prepare_attachment_access","arguments":{"attachment_ids":[__ATTACHMENT_ID__],"access_kind":"managed_export_file"}}}
            """.Replace("__ATTACHMENT_ID__", attachmentId.ToString());
        var input = new StringReader(inputText);
        var server = new McpStdioServer(configStore, database, input, output, error);

        await server.RunAsync(CancellationToken.None);

        var lines = OutputLines(output);
        var searchPayload = JsonNode.Parse(lines[1]).AsObject()["result"]["structuredContent"].AsObject();
        var textPayload = JsonNode.Parse(lines[2]).AsObject()["result"]["structuredContent"].AsObject();
        var accessPayload = JsonNode.Parse(lines[3]).AsObject()["result"]["structuredContent"].AsObject();
        var searchAttachment = searchPayload["results"].AsArray()[0]["matching_attachments"].AsArray()[0].AsObject();
        var access = accessPayload["attachments"].AsArray()[0]["access"].AsObject();
        var auditRows = ReadStrings(
            temp.Paths.DatabasePath,
            "SELECT tool_name || '|' || result_summary || '|' || COALESCE(affected_message_ids, '') || '|' || COALESCE(affected_attachment_ids, '') FROM audit_log ORDER BY id;");

        Assert.Equal("ready", searchPayload["status"].GetValue<string>());
        Assert.Equal(attachmentId, searchAttachment["attachment_id"].GetValue<int>());
        Assert.Equal("statement.txt", searchAttachment["display_path"].GetValue<string>());
        Assert.Equal("ok", textPayload["status"].GetValue<string>());
        Assert.Contains("DDV", textPayload["attachment"]["combined_text"].GetValue<string>(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal("ok", accessPayload["status"].GetValue<string>());
        Assert.Equal("managed_export_file", access["kind"].GetValue<string>());
        Assert.True(File.Exists(access["path"].GetValue<string>()));
        Assert.Contains($"email_get_attachment_text|status=ok attachment=1|{messageId}|{attachmentId}", auditRows);
        Assert.Contains($"email_prepare_attachment_access|prepared=1 failed=0|{messageId}|{attachmentId}", auditRows);
    }

    [Fact]
    public async Task EmailAttachmentFailureToolsListRetryAndAuditWithoutLeakingDiagnostics()
    {
        using var temp = TempWorkspace.Create();
        var account = TestData.Account();
        var configStore = new ConfigStore(temp.Paths);
        var config = new AppConfig();
        config.UpsertAccount(account);
        configStore.Save(config);
        var database = new EmailDatabase(temp.Paths);
        var accountId = database.UpsertConfiguredAccount(account);
        database.UpsertFolders(accountId, [TestData.Folder("Inbox", role: "inbox")]);
        var inbox = database.ReadFolders("yahoo").Single();
        database.UpsertMessageMetadataBatch(accountId, inbox.Id, [
            TestData.Message(
                "100",
                "emailid:mcp-failed-attachment",
                "mcp-failed-attachment@example.com",
                hasAttachments: true)
        ], SyncStateJson(1, 1, 1), 100);
        var messageId = ReadInts(temp.Paths.DatabasePath, "SELECT id FROM messages;").Single();
        var processor = new AttachmentProcessor(new AttachmentObjectStore(temp.Paths));
        database.UpsertMessageBody(new(
            messageId,
            "Attachment body",
            null,
            "Attachment body",
            [],
            [processor.ProcessEmailAttachment(
                "2", "payload.bin", "application/octet-stream", 3, [1, 2, 3], "payload.bin")]));
        var attachmentId = ReadInts(temp.Paths.DatabasePath, "SELECT id FROM attachments;").Single();

        var output = new StringWriter();
        var error = new StringWriter();
        var inputText = """
            {"jsonrpc":"2.0","id":1,"method":"initialize","params":{"protocolVersion":"2025-11-25","capabilities":{},"clientInfo":{"name":"test-client","version":"1.0.0"}}}
            {"jsonrpc":"2.0","method":"notifications/initialized"}
            {"jsonrpc":"2.0","id":2,"method":"tools/call","params":{"name":"email_list_attachment_extraction_failures","arguments":{"attachment_ids":[__ATTACHMENT_ID__],"status":"open","limit":10}}}
            {"jsonrpc":"2.0","id":3,"method":"tools/call","params":{"name":"email_retry_attachment_extraction","arguments":{"attachment_ids":[__ATTACHMENT_ID__],"limit":10}}}
            """.Replace("__ATTACHMENT_ID__", attachmentId.ToString());
        var input = new StringReader(inputText);
        var server = new McpStdioServer(configStore, database, input, output, error);

        await server.RunAsync(CancellationToken.None);

        var lines = OutputLines(output);
        var listed = JsonNode.Parse(lines[1]).AsObject()["result"]["structuredContent"].AsObject();
        var retried = JsonNode.Parse(lines[2]).AsObject()["result"]["structuredContent"].AsObject();
        var listedFailure = listed["failures"].AsArray().Single().AsObject();
        var auditRows = ReadStrings(
            temp.Paths.DatabasePath,
            "SELECT tool_name || '|' || result_summary || '|' || COALESCE(affected_attachment_ids, '') FROM audit_log ORDER BY id;");

        Assert.Equal("unsupported_attachment_type", listedFailure["error_code"].GetValue<string>());
        Assert.Null(listedFailure["storage_key"]);
        Assert.Null(listedFailure["exception_details"]);
        Assert.Equal("completed_with_failures", retried["status"].GetValue<string>());
        Assert.Equal(1, retried["failed"].GetValue<int>());
        Assert.Contains($"email_list_attachment_extraction_failures|status=ok failures=1|{attachmentId}", auditRows);
        Assert.Contains($"email_retry_attachment_extraction|selected=1 succeeded=0 failed=1 skipped=0|{attachmentId}", auditRows);
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
