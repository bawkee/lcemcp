using System.Text.Json;
using System.Text.Json.Nodes;

namespace LceMcp;

internal sealed class McpStdioServer
{
    private const string LatestProtocolVersion = "2025-11-25";
    private static readonly HashSet<string> SupportedProtocolVersions = new(StringComparer.Ordinal)
    {
        "2025-11-25",
        "2025-06-18",
        "2024-11-05"
    };

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false
    };

    private readonly ConfigStore _configStore;
    private readonly WindowsCredentialStore _credentialStore;
    private readonly EmailDatabase _database;
    private readonly TextReader _input;
    private readonly TextWriter _output;
    private readonly TextWriter _error;
    private string _clientName;

    public McpStdioServer(
        ConfigStore configStore,
        EmailDatabase database,
        TextReader input,
        TextWriter output,
        TextWriter error,
        WindowsCredentialStore credentialStore = null)
    {
        _configStore = configStore;
        _database = database;
        _input = input;
        _output = output;
        _error = error;
        _credentialStore = credentialStore ?? new WindowsCredentialStore();
    }

    public static Task<int> RunAsync(
        ConfigStore configStore,
        EmailDatabase database,
        CancellationToken cancellationToken) =>
        new McpStdioServer(configStore, database, Console.In, Console.Out, Console.Error)
            .RunAsync(cancellationToken);

    public async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        await _error.WriteLineAsync("lcemcp MCP stdio server started.");

        while (!cancellationToken.IsCancellationRequested)
        {
            var line = await _input.ReadLineAsync(cancellationToken);
            if (line is null)
                break;

            if (string.IsNullOrWhiteSpace(line))
                continue;

            await HandleLineAsync(line, cancellationToken);
        }

        return 0;
    }

    private async Task HandleLineAsync(string line, CancellationToken cancellationToken)
    {
        JsonDocument document;

        try
        {
            document = JsonDocument.Parse(line);
        }
        catch (JsonException)
        {
            await WriteErrorAsync(null, -32700, "Parse error", cancellationToken);
            return;
        }

        using (document)
        {
            await HandleMessageAsync(document.RootElement, cancellationToken);
        }
    }

    private async Task HandleMessageAsync(JsonElement message, CancellationToken cancellationToken)
    {
        if (message.ValueKind != JsonValueKind.Object)
        {
            await WriteErrorAsync(null, -32600, "Invalid Request", cancellationToken);
            return;
        }

        var id = ReadId(message);
        var hasId = message.TryGetProperty("id", out _);

        if (!message.TryGetProperty("method", out var methodElement))
            return;

        if (methodElement.ValueKind != JsonValueKind.String)
        {
            if (hasId)
                await WriteErrorAsync(id, -32600, "Invalid Request", cancellationToken);
            return;
        }

        var method = methodElement.GetString();

        try
        {
            var result = method switch
            {
                "initialize" => HandleInitialize(message),
                "ping" => new JsonObject(),
                "tools/list" => HandleToolsList(),
                "tools/call" => HandleToolsCall(message, cancellationToken),
                "notifications/initialized" => null,
                "notifications/cancelled" => null,
                _ => throw new JsonRpcError(-32601, $"Method not found: {method}")
            };

            if (hasId && result is not null)
                await WriteResultAsync(id, result, cancellationToken);
        }
        catch (JsonRpcError ex)
        {
            if (hasId)
                await WriteErrorAsync(id, ex.Code, ex.Message, cancellationToken);
        }
        catch (Exception ex)
        {
            await _error.WriteLineAsync($"MCP request failed: {ex}");
            if (hasId)
                await WriteErrorAsync(id, -32603, "Internal server error", cancellationToken);
        }
    }

    private JsonObject HandleInitialize(JsonElement message)
    {
        var requestedVersion = TryGetString(message, "params", "protocolVersion");
        var protocolVersion = !string.IsNullOrWhiteSpace(requestedVersion)
            && SupportedProtocolVersions.Contains(requestedVersion)
                ? requestedVersion
                : LatestProtocolVersion;

        _clientName = TryGetString(message, "params", "clientInfo", "name");

        return new()
        {
            ["protocolVersion"] = protocolVersion,
            ["capabilities"] = new JsonObject
            {
                ["tools"] = new JsonObject
                {
                    ["listChanged"] = false
                }
            },
            ["serverInfo"] = new JsonObject
            {
                ["name"] = "lcemcp",
                ["title"] = "Locally Cached Email MCP",
                ["version"] = typeof(McpStdioServer).Assembly.GetName().Version?.ToString(3) ?? "0.0.0"
            },
            ["instructions"] = "Use email_get_sync_status before trusting email_search. If email_search returns not_synced, call email_sync_now and poll email_get_sync_status; not_synced is not evidence that no matching email exists."
        };
    }

    private static JsonObject HandleToolsList() =>
        new()
        {
            ["tools"] = new JsonArray
            {
                ToolDefinition(
                    "email_list_accounts",
                    "Email Accounts",
                    "List configured local email accounts and lightweight sync/readiness state.",
                    ObjectSchema(new()
                    {
                        ["enabled_only"] = BoolSchema("Only include enabled accounts. Defaults to true.", defaultValue: true)
                    }),
                    readOnly: true,
                    idempotent: true),
                ToolDefinition(
                    "email_list_folders",
                    "Email Folders",
                    "List known locally cached mail folders for configured accounts.",
                    ObjectSchema(new()
                    {
                        ["accounts"] = StringArraySchema("Optional account ids, display names, or email addresses. Omit, null, or [] for all configured accounts."),
                        ["sync_enabled_only"] = BoolSchema("Only include selectable folders enabled for sync. Defaults to false.", defaultValue: false)
                    }),
                    readOnly: true,
                    idempotent: true),
                ToolDefinition(
                    "email_search",
                    "Email Search",
                    "Search the local indexed message corpus. Returns not_synced instead of empty results when the requested corpus is incomplete.",
                    ObjectSchema(new()
                    {
                        ["query"] = StringSchema("Search text. Quoted phrases and OR are supported."),
                        ["accounts"] = StringArraySchema("Optional account ids, display names, or email addresses. Omit, null, or [] for all configured accounts."),
                        ["from_email"] = NullableStringSchema("Optional exact sender email filter."),
                        ["folder_roles"] = StringArraySchema("Optional folder-role filter such as inbox, sent, archive, trash, or custom."),
                        ["has_attachment"] = NullableBoolSchema("Optional attachment metadata filter."),
                        ["limit"] = IntSchema("Maximum result count, 1 to 100. Defaults to 20.", 1, 100, 20),
                        ["snippet_chars"] = IntSchema("Approximate maximum snippet characters, 160 to 4096. Defaults to 1024.", 160, 4096, 1024),
                        ["allow_partial"] = BoolSchema("Debug opt-in. Search even when readiness is incomplete and label results partial.", defaultValue: false)
                    }, "query"),
                    readOnly: true,
                    idempotent: true),
                ToolDefinition(
                    "email_get_message",
                    "Email Message",
                    "Fetch one locally cached message by stable local message_id, with bounded body text and recipient/folder context.",
                    ObjectSchema(new()
                    {
                        ["message_id"] = IntSchema("Stable local message id returned by email_search.", 1, int.MaxValue, null),
                        ["include_body"] = BoolSchema("Include normalized body text when available. Defaults to true.", defaultValue: true),
                        ["include_attachments"] = BoolSchema("Include attachment metadata when available. Defaults to true.", defaultValue: true),
                        ["max_body_chars"] = IntSchema("Maximum returned body characters, 500 to 50000. Defaults to 20000.", 500, 50000, 20000)
                    }, "message_id"),
                    readOnly: true,
                    idempotent: true),
                ToolDefinition(
                    "email_sync_now",
                    "Email Sync",
                    "Start or queue local metadata and body indexing for configured accounts. Returns quickly with a sync_run_id; poll email_get_sync_status for progress.",
                    ObjectSchema(new()
                    {
                        ["accounts"] = StringArraySchema("Optional account ids, display names, or email addresses. Omit, null, or [] for all enabled accounts."),
                        ["folder"] = NullableStringSchema("Optional folder path or name to sync. Omit for all sync-enabled folders."),
                        ["full"] = BoolSchema("Use no date bound when syncing metadata. Defaults to false, which uses account history_days.", defaultValue: false),
                        ["since_days"] = IntSchema("Optional metadata date bound override. Use 0 for no date bound.", 0, 3650, null),
                        ["max_per_folder"] = IntSchema("Optional per-folder cap. Use 0 for no cap; capped runs usually remain not_synced.", 0, 1000000, 0),
                        ["wait_for_completion"] = BoolSchema("Accepted for compatibility, but the MCP server still returns immediately; poll email_get_sync_status.", defaultValue: false)
                    }),
                    readOnly: false,
                    idempotent: false,
                    openWorld: true),
                ToolDefinition(
                    "email_get_sync_status",
                    "Email Sync Status",
                    "Return local email sync progress and binary message-search readiness.",
                    ObjectSchema(new()
                    {
                        ["accounts"] = StringArraySchema("Optional account ids, display names, or email addresses. Omit, null, or [] for all configured accounts."),
                        ["include_folders"] = BoolSchema("Whether to include known local folder sync state for each account. Defaults to true.", defaultValue: true)
                    }),
                    readOnly: true,
                    idempotent: true)
            }
        };

    private JsonObject HandleToolsCall(JsonElement message, CancellationToken cancellationToken)
    {
        if (!message.TryGetProperty("params", out var parameters)
            || parameters.ValueKind != JsonValueKind.Object)
            throw new JsonRpcError(-32602, "tools/call params must be an object.");

        var toolName = ReadRequiredString(parameters, "name");
        var arguments = parameters.TryGetProperty("arguments", out var rawArguments)
            ? rawArguments
            : default;

        try
        {
            var execution = ExecuteTool(toolName, arguments, cancellationToken);
            _database.WriteAuditLog(
                _clientName,
                toolName,
                execution.ActionType,
                execution.ArgumentsSummary,
                execution.ResultSummary,
                execution.AffectedMessageIds);
            return ToolResult(execution.Payload, execution.IsError);
        }
        catch (JsonRpcError)
        {
            _database.WriteAuditLog(_clientName, toolName, "read", "invalid", "failed");
            throw;
        }
        catch
        {
            _database.WriteAuditLog(_clientName, toolName, "read", "unknown", "failed");
            throw;
        }
    }

    private ToolExecution ExecuteTool(
        string toolName,
        JsonElement arguments,
        CancellationToken cancellationToken) =>
        toolName switch
        {
            "email_list_accounts" => ExecuteListAccounts(arguments),
            "email_list_folders" => ExecuteListFolders(arguments),
            "email_search" => ExecuteSearch(arguments),
            "email_get_message" => ExecuteGetMessage(arguments),
            "email_sync_now" => ExecuteSyncNow(arguments, cancellationToken),
            "email_get_sync_status" => ExecuteGetSyncStatus(arguments),
            _ => throw new JsonRpcError(-32602, $"Unknown tool: {toolName}")
        };

    private ToolExecution ExecuteListAccounts(JsonElement arguments)
    {
        EnsureArgumentObject(arguments, "email_list_accounts");
        ValidateAllowedArguments(arguments, "enabled_only", "_meta");

        var enabledOnly = ReadOptionalBool(arguments, "enabled_only", defaultValue: true);
        var config = _configStore.Load();
        var accounts = SelectAccounts(config, [], enabledOnly);
        var values = new JsonArray();

        foreach (var account in accounts)
        {
            var summary = _database.ReadAccountSyncSummary(account.Id);
            var readiness = _database.GetMessageSearchReadiness(new(
                AccountFilters: [account.Id],
                FromEmail: null,
                FolderRoles: [],
                HasAttachment: null));

            values.Add(new JsonObject
            {
                ["account_id"] = NumberOrNull(summary?.AccountId),
                ["name"] = account.Id,
                ["display_name"] = account.DisplayName,
                ["email_address"] = account.EmailAddress,
                ["provider_preset"] = account.Provider,
                ["enabled"] = account.Enabled,
                ["history_days"] = account.HistoryDays,
                ["last_success_at"] = StringOrNull(summary?.LastSuccessAt),
                ["last_error_at"] = StringOrNull(summary?.LastErrorAt),
                ["last_error"] = StringOrNull(summary?.LastError),
                ["search_ready"] = readiness.SearchReady
            });
        }

        var payload = new JsonObject
        {
            ["accounts"] = values
        };

        return new(payload, "read", $"enabled_only={enabledOnly.ToString().ToLowerInvariant()}", $"accounts={values.Count}");
    }

    private ToolExecution ExecuteListFolders(JsonElement arguments)
    {
        EnsureArgumentObject(arguments, "email_list_folders");
        ValidateAllowedArguments(arguments, "accounts", "sync_enabled_only", "_meta");

        var accountFilters = ReadOptionalStringArray(arguments, "accounts");
        var syncEnabledOnly = ReadOptionalBool(arguments, "sync_enabled_only", defaultValue: false);
        var config = _configStore.Load();
        var accounts = SelectAccounts(config, accountFilters, enabledOnly: false);
        var values = new JsonArray();

        foreach (var account in accounts)
        {
            var folders = _database.ReadFolders(account.Id);
            if (syncEnabledOnly)
                folders = folders.Where(folder => folder.Selectable && folder.SyncEnabled).ToList();

            foreach (var folder in folders)
            {
                values.Add(new JsonObject
                {
                    ["folder_id"] = folder.Id,
                    ["account"] = folder.AccountName,
                    ["account_email_address"] = folder.AccountEmailAddress,
                    ["name"] = folder.Name,
                    ["path"] = folder.Path,
                    ["role"] = folder.Role,
                    ["selectable"] = folder.Selectable,
                    ["sync_enabled"] = folder.SyncEnabled,
                    ["last_sync_at"] = StringOrNull(folder.LastSyncAt),
                    ["last_discovered_at"] = StringOrNull(folder.LastDiscoveredAt)
                });
            }
        }

        var payload = new JsonObject
        {
            ["folders"] = values
        };

        return new(
            payload,
            "read",
            $"accounts={FormatAccountFilters(accountFilters)}, sync_enabled_only={syncEnabledOnly.ToString().ToLowerInvariant()}",
            $"folders={values.Count}");
    }

    private ToolExecution ExecuteSearch(JsonElement arguments)
    {
        EnsureArgumentObject(arguments, "email_search");
        ValidateAllowedArguments(
            arguments,
            "query",
            "accounts",
            "from_email",
            "folder_roles",
            "has_attachment",
            "limit",
            "snippet_chars",
            "allow_partial",
            "_meta");

        var query = ReadRequiredString(arguments, "query").Trim();
        var accountFilters = ReadOptionalStringArray(arguments, "accounts");
        var configuredAccounts = SelectAccounts(_configStore.Load(), accountFilters, enabledOnly: true);
        var effectiveAccountFilters = configuredAccounts.Select(account => account.Id).ToList();
        var request = new EmailSearchRequest(
            Query: query,
            AccountFilters: effectiveAccountFilters,
            FromEmail: ReadOptionalString(arguments, "from_email"),
            FolderRoles: ReadOptionalStringArray(arguments, "folder_roles"),
            HasAttachment: ReadOptionalNullableBool(arguments, "has_attachment"),
            Limit: ReadOptionalInt(arguments, "limit", defaultValue: 20, min: 1, max: 100),
            SnippetChars: ReadOptionalInt(arguments, "snippet_chars", defaultValue: 1024, min: 160, max: 4096),
            AllowPartial: ReadOptionalBool(arguments, "allow_partial", defaultValue: false));

        try
        {
            FtsQueryBuilder.Build(request.Query);
        }
        catch (CliException ex)
        {
            throw new JsonRpcError(-32602, ex.Message);
        }

        var argumentsSummary = $"query_length={request.Query.Length}, accounts={FormatAccountFilters(request.AccountFilters)}, limit={request.Limit}";

        if (configuredAccounts.Count == 0)
        {
            var noAccountsReadiness = EmptyReadiness(_database.ReadActiveSyncRun());
            var payload = new JsonObject
            {
                ["status"] = "not_synced",
                ["search_ready"] = false,
                ["message"] = "No enabled configured accounts matched the requested search scope.",
                ["sync_run_id"] = StringOrNull(noAccountsReadiness.ActiveSyncRun?.Id),
                ["readiness"] = ToReadinessJson(noAccountsReadiness),
                ["progress"] = noAccountsReadiness.ActiveSyncRun is null ? null : ToSyncProgressJson(noAccountsReadiness.ActiveSyncRun),
                ["results"] = new JsonArray(),
                ["has_more"] = false,
                ["next_cursor"] = null
            };

            return new(payload, "read", argumentsSummary, "status=not_synced results=not_run");
        }

        var readiness = _database.GetMessageSearchReadiness(new(
            AccountFilters: request.AccountFilters,
            FromEmail: request.FromEmail,
            FolderRoles: request.FolderRoles,
            HasAttachment: request.HasAttachment));

        if (!readiness.SearchReady && !request.AllowPartial)
        {
            var payload = new JsonObject
            {
                ["status"] = "not_synced",
                ["search_ready"] = false,
                ["message"] = "The requested email search corpus is not fully indexed.",
                ["sync_run_id"] = StringOrNull(readiness.ActiveSyncRun?.Id),
                ["readiness"] = ToReadinessJson(readiness),
                ["progress"] = readiness.ActiveSyncRun is null ? null : ToSyncProgressJson(readiness.ActiveSyncRun),
                ["results"] = new JsonArray(),
                ["has_more"] = false,
                ["next_cursor"] = null
            };

            return new(payload, "read", argumentsSummary, "status=not_synced results=not_run");
        }

        var rawResults = _database.SearchMessages(request with { Limit = Math.Min(request.Limit + 1, 101) });
        var hasMore = rawResults.Count > request.Limit;
        var results = rawResults.Take(request.Limit).ToList();
        var payloadResults = new JsonArray();

        foreach (var result in results)
            payloadResults.Add(ToSearchResultJson(result));

        var status = readiness.SearchReady ? "ready" : "partial";
        var payloadObject = new JsonObject
        {
            ["status"] = status,
            ["search_ready"] = readiness.SearchReady,
            ["results_may_be_incomplete"] = !readiness.SearchReady,
            ["readiness"] = ToReadinessJson(readiness),
            ["results"] = payloadResults,
            ["has_more"] = hasMore,
            ["next_cursor"] = null
        };

        return new(
            payloadObject,
            "read",
            argumentsSummary,
            $"status={status} results={results.Count} has_more={hasMore.ToString().ToLowerInvariant()}",
            results.Select(result => result.MessageId).ToList());
    }

    private ToolExecution ExecuteGetMessage(JsonElement arguments)
    {
        EnsureArgumentObject(arguments, "email_get_message");
        ValidateAllowedArguments(arguments, "message_id", "include_body", "include_attachments", "max_body_chars", "_meta");

        var messageId = ReadRequiredInt(arguments, "message_id", min: 1, max: int.MaxValue);
        var includeBody = ReadOptionalBool(arguments, "include_body", defaultValue: true);
        var includeAttachments = ReadOptionalBool(arguments, "include_attachments", defaultValue: true);
        var maxBodyChars = ReadOptionalInt(arguments, "max_body_chars", defaultValue: 20000, min: 500, max: 50000);
        var message = _database.ReadMessage(messageId);
        var argumentsSummary = $"message_id={messageId}, include_body={includeBody.ToString().ToLowerInvariant()}, include_attachments={includeAttachments.ToString().ToLowerInvariant()}";

        if (message is null || !IsConfiguredAccount(_configStore.Load(), message.AccountName, message.AccountEmailAddress))
        {
            var notFound = new JsonObject
            {
                ["status"] = "not_found",
                ["message"] = $"Message {messageId} is not available in configured local storage."
            };

            return new(notFound, "read", argumentsSummary, "status=not_found", IsError: true);
        }

        var bodyText = includeBody ? message.BodyText : null;
        var truncated = bodyText is not null && bodyText.Length > maxBodyChars;
        if (truncated)
            bodyText = bodyText[..maxBodyChars];

        var payload = new JsonObject
        {
            ["status"] = "ok",
            ["message"] = new JsonObject
            {
                ["message_id"] = message.MessageId,
                ["account"] = message.AccountName,
                ["account_email_address"] = message.AccountEmailAddress,
                ["folders"] = ToJsonArray(message.Folders),
                ["date_sent"] = StringOrNull(message.DateSent),
                ["date_received"] = StringOrNull(message.DateReceived),
                ["from"] = ToPersonJson(message.FromName, message.FromEmail),
                ["to"] = ToRecipientArray(message.Recipients, "to"),
                ["cc"] = ToRecipientArray(message.Recipients, "cc"),
                ["bcc"] = ToRecipientArray(message.Recipients, "bcc"),
                ["reply_to"] = ToRecipientArray(message.Recipients, "reply-to"),
                ["subject"] = StringOrNull(message.Subject),
                ["body_text"] = StringOrNull(bodyText),
                ["body_available"] = message.BodyText is not null,
                ["body_truncated"] = truncated,
                ["has_attachments"] = message.HasAttachments,
                ["attachments"] = includeAttachments ? new JsonArray() : null,
                ["attachment_metadata_available"] = false
            }
        };

        return new(payload, "read", argumentsSummary, "status=ok message=1", [message.MessageId]);
    }

    private ToolExecution ExecuteSyncNow(JsonElement arguments, CancellationToken cancellationToken)
    {
        EnsureArgumentObject(arguments, "email_sync_now");
        ValidateAllowedArguments(
            arguments,
            "accounts",
            "folder",
            "full",
            "since_days",
            "max_per_folder",
            "wait_for_completion",
            "_meta");

        var request = new McpSyncNowRequest(
            Accounts: ReadOptionalStringArray(arguments, "accounts"),
            Folder: ReadOptionalString(arguments, "folder"),
            Full: ReadOptionalBool(arguments, "full", defaultValue: false),
            SinceDays: ReadOptionalNullableInt(arguments, "since_days", min: 0, max: 3650),
            MaxPerFolder: ReadOptionalInt(arguments, "max_per_folder", defaultValue: 0, min: 0, max: 1000000),
            WaitForCompletion: ReadOptionalBool(arguments, "wait_for_completion", defaultValue: false));
        var config = _configStore.Load();
        var accounts = SelectAccounts(config, request.Accounts, enabledOnly: true);
        var argumentsSummary = $"accounts={FormatAccountFilters(request.Accounts)}, folder={FormatOptional(request.Folder)}, full={request.Full.ToString().ToLowerInvariant()}, max_per_folder={request.MaxPerFolder}";

        if (accounts.Count == 0)
        {
            var payload = new JsonObject
            {
                ["accepted"] = false,
                ["status"] = "failed",
                ["message"] = "No enabled configured accounts matched the request."
            };

            return new(payload, "sync", argumentsSummary, "accepted=false reason=no_accounts", IsError: true);
        }

        var works = new List<SyncAccountWork>();
        var databaseIds = _database.UpsertConfiguredAccounts(accounts);
        for (var i = 0; i < accounts.Count; i++)
        {
            var account = accounts[i];
            var validationErrors = AccountConfigValidator.ValidateForImap(account).ToList();
            if (validationErrors.Count > 0)
                return SyncRejected(argumentsSummary, $"Account '{account.Id}' is not valid for IMAP: {string.Join("; ", validationErrors)}");

            var password = _credentialStore.Read(account.CredentialRef);
            if (password is null)
                return SyncRejected(argumentsSummary, $"Credential not found for account '{account.Id}': {account.CredentialRef}");

            works.Add(new(account, databaseIds[i], password));
        }

        var initialTotal = works.Sum(work => _database.ReadSyncFolders(work.Account.Id, request.Folder).Count);
        if (initialTotal == 0)
            initialTotal = works.Count;

        var syncRun = _database.StartOrQueueSyncRun(
            accounts.Count == 1 ? works[0].DatabaseAccountId : null,
            accounts.Count == 1 ? works[0].Account.Id : "multiple",
            request.Folder,
            "syncing_metadata",
            initialTotal);

        StartBackgroundSync(request, works, syncRun, cancellationToken);

        var payloadObject = new JsonObject
        {
            ["accepted"] = true,
            ["sync_run_id"] = syncRun.Id,
            ["status"] = syncRun.Status,
            ["active_sync_run_id"] = StringOrNull(syncRun.ActiveRun?.Id),
            ["message"] = syncRun.Acquired
                ? "Sync started. Poll email_get_sync_status for progress."
                : "A sync is already running or queued; this request is queued. Poll email_get_sync_status for progress.",
            ["wait_for_completion_honored"] = false,
            ["progress"] = syncRun.ActiveRun is null ? null : ToSyncProgressJson(syncRun.ActiveRun)
        };

        return new(payloadObject, "sync", argumentsSummary, $"accepted=true status={syncRun.Status} sync_run_id={syncRun.Id}");
    }

    private ToolExecution ExecuteGetSyncStatus(JsonElement arguments)
    {
        var request = ParseSyncStatusRequest(arguments);
        var argumentsSummary = $"accounts={FormatAccountFilters(request.Accounts)}, include_folders={request.IncludeFolders.ToString().ToLowerInvariant()}";
        var payload = BuildSyncStatusResponse(request);
        var resultSummary = SummarizeSyncStatus(payload);
        return new(payload, "read", argumentsSummary, resultSummary);
    }

    private static ToolExecution SyncRejected(string argumentsSummary, string message)
    {
        var payload = new JsonObject
        {
            ["accepted"] = false,
            ["status"] = "failed",
            ["message"] = message
        };

        return new(payload, "sync", argumentsSummary, "accepted=false reason=validation", IsError: true);
    }

    private JsonObject BuildSyncStatusResponse(McpSyncStatusRequest request)
    {
        var databaseStatus = _database.GetStatus();
        var activeSyncRun = _database.ReadActiveSyncRun();
        var config = _configStore.Load();
        var accounts = SelectAccounts(config, request.Accounts, enabledOnly: false);
        var accountStatuses = new JsonArray();

        foreach (var account in accounts)
            accountStatuses.Add(BuildAccountStatus(account, request.IncludeFolders, activeSyncRun));

        return new()
        {
            ["database_locked"] = false,
            ["schema_version"] = databaseStatus.SchemaVersion,
            ["target_schema_version"] = databaseStatus.TargetSchemaVersion,
            ["active_sync_run_id"] = StringOrNull(activeSyncRun?.Id),
            ["accounts"] = accountStatuses
        };
    }

    private JsonObject BuildAccountStatus(
        AccountConfig account,
        bool includeFolders,
        SyncRunSnapshot activeSyncRun)
    {
        var readiness = _database.GetMessageSearchReadiness(new(
            AccountFilters: [account.Id],
            FromEmail: null,
            FolderRoles: [],
            HasAttachment: null));
        var syncSummary = _database.ReadAccountSyncSummary(account.Id);
        var folders = includeFolders ? _database.ReadFolders(account.Id) : [];
        var validationErrors = AccountConfigValidator.ValidateForImap(account).ToList();

        return new()
        {
            ["account"] = account.Id,
            ["database_account_id"] = NumberOrNull(syncSummary?.AccountId),
            ["display_name"] = account.DisplayName,
            ["email_address"] = account.EmailAddress,
            ["provider_preset"] = account.Provider,
            ["enabled"] = account.Enabled,
            ["config_valid"] = validationErrors.Count == 0,
            ["config_errors"] = ToJsonArray(validationErrors),
            ["last_success_at"] = StringOrNull(syncSummary?.LastSuccessAt),
            ["last_error_at"] = StringOrNull(syncSummary?.LastErrorAt),
            ["last_error"] = StringOrNull(syncSummary?.LastError),
            ["search_ready"] = readiness.SearchReady,
            ["search_ready_scope"] = new JsonObject
            {
                ["history_days"] = account.HistoryDays,
                ["message_search_ready"] = readiness.SearchReady,
                ["attachment_search_ready"] = false
            },
            ["readiness"] = ToReadinessJson(readiness),
            ["sync_progress"] = IsActiveForAccount(activeSyncRun, account)
                ? ToSyncProgressJson(activeSyncRun)
                : null,
            ["metadata_messages"] = readiness.MetadataMessages,
            ["message_bodies_indexed"] = readiness.IndexedMessageBodies,
            ["messages_indexed"] = readiness.MessageSearchDocs,
            ["attachments_indexed"] = 0,
            ["extraction_pending"] = 0,
            ["folders"] = includeFolders ? ToFoldersJson(folders) : null
        };
    }

    private void StartBackgroundSync(
        McpSyncNowRequest request,
        IReadOnlyList<SyncAccountWork> works,
        SyncRunStartResult syncRun,
        CancellationToken cancellationToken)
    {
        _ = Task.Run(async () =>
        {
            try
            {
                await RunBackgroundSyncAsync(request, works, syncRun, cancellationToken);
            }
            catch (Exception ex)
            {
                await _error.WriteLineAsync($"Background sync {syncRun.Id} failed outside normal completion: {ex}");
            }
        }, CancellationToken.None);
    }

    private async Task RunBackgroundSyncAsync(
        McpSyncNowRequest request,
        IReadOnlyList<SyncAccountWork> works,
        SyncRunStartResult syncRun,
        CancellationToken cancellationToken)
    {
        var currentRun = syncRun;

        try
        {
            currentRun = await WaitForSyncRunLeaseAsync(currentRun, cancellationToken);

            await RunWithSyncLeaseHeartbeatAsync(currentRun, cancellationToken, async () =>
            {
                var metadataFailures = await RunMetadataSyncPhaseAsync(request, works, currentRun, cancellationToken);
                var bodyFailures = await RunBodySyncPhaseAsync(request, works, currentRun, cancellationToken);
                var failures = metadataFailures.Concat(bodyFailures).ToList();
                var active = _database.ReadActiveSyncRun();
                var total = active?.Total ?? 0;
                var done = failures.Count == 0 ? total : active?.Done ?? 0;
                var completed = _database.CompleteSyncRun(
                    currentRun.Id,
                    currentRun.OwnerId,
                    succeeded: failures.Count == 0,
                    done: done,
                    total: total,
                    lastError: failures.Count == 0 ? null : string.Join("; ", failures.Take(10)));
                if (!completed)
                    throw new OperationCanceledException("Sync lease was lost.");
            });
        }
        catch (OperationCanceledException)
        {
            _database.CancelSyncRun(currentRun.Id, currentRun.OwnerId, done: 0, total: 0);
        }
        catch (Exception ex)
        {
            _database.CompleteSyncRun(currentRun.Id, currentRun.OwnerId, succeeded: false, done: 0, total: 0, lastError: ex.Message);
            await _error.WriteLineAsync($"Background sync {currentRun.Id} failed: {ex.Message}");
        }
    }

    private async Task<IReadOnlyList<string>> RunMetadataSyncPhaseAsync(
        McpSyncNowRequest request,
        IReadOnlyList<SyncAccountWork> works,
        SyncRunStartResult syncRun,
        CancellationToken cancellationToken)
    {
        var foldersByAccount = new Dictionary<string, IReadOnlyList<StoredFolder>>(StringComparer.OrdinalIgnoreCase);
        var failures = new List<string>();
        var totalFolders = 0;

        foreach (var work in works)
        {
            var folders = _database.ReadSyncFolders(work.Account.Id, request.Folder);
            if (folders.Count == 0 && _database.ReadFolders(work.Account.Id).Count == 0)
            {
                var discovery = await new ImapFolderDiscovery().DiscoverAsync(work.Account, work.Password, cancellationToken);
                _database.UpsertFolders(work.DatabaseAccountId, discovery.Folders);
                folders = _database.ReadSyncFolders(work.Account.Id, request.Folder);
            }

            foldersByAccount[work.Account.Id] = folders;
            totalFolders += folders.Count;

            if (folders.Count == 0)
                failures.Add($"No syncable folders found for {work.Account.Id}");
        }

        UpdateSyncRunPhaseOrThrow(syncRun, "syncing_metadata", 0, totalFolders);

        var doneOffset = 0;
        foreach (var work in works)
        {
            var folders = foldersByAccount[work.Account.Id];
            if (folders.Count == 0)
                continue;

            var sinceDays = request.Full ? 0 : request.SinceDays ?? work.Account.HistoryDays;
            var result = await new ImapMetadataSync(_database).SyncAccountAsync(
                work.Account,
                work.Password,
                work.DatabaseAccountId,
                folders,
                sinceDays,
                request.MaxPerFolder,
                batchSize: 50,
                cancellationToken,
                (done, _) => UpdateSyncRunProgressOrThrow(syncRun, doneOffset + done, totalFolders),
                () => ThrowIfSyncLeaseLost(syncRun));

            failures.AddRange(result.Folders
                .Where(folder => !folder.Succeeded)
                .Select(folder => $"{work.Account.Id}/{folder.FolderPath}: {folder.Error}"));
            doneOffset += folders.Count;
        }

        return failures;
    }

    private async Task<IReadOnlyList<string>> RunBodySyncPhaseAsync(
        McpSyncNowRequest request,
        IReadOnlyList<SyncAccountWork> works,
        SyncRunStartResult syncRun,
        CancellationToken cancellationToken)
    {
        var totalTargets = works.Sum(work => _database.ReadPendingBodySyncTargets(work.Account.Id, request.Folder, request.MaxPerFolder).Count);
        UpdateSyncRunPhaseOrThrow(syncRun, "syncing_bodies", 0, totalTargets);

        var failures = new List<string>();
        var doneOffset = 0;

        foreach (var work in works)
        {
            var result = await new ImapBodySync(_database).SyncAccountAsync(
                work.Account,
                work.Password,
                request.Folder,
                request.MaxPerFolder,
                batchSize: 10,
                cancellationToken,
                (done, _) => UpdateSyncRunProgressOrThrow(syncRun, doneOffset + done, totalTargets),
                () => ThrowIfSyncLeaseLost(syncRun));

            var selected = result.Folders.Sum(folder => folder.SelectedCount);
            failures.AddRange(result.Folders
                .Where(folder => !folder.Succeeded)
                .Select(folder => $"{work.Account.Id}/{folder.FolderPath}: {folder.Error}"));
            doneOffset += selected;
        }

        return failures;
    }

    private async Task<SyncRunStartResult> WaitForSyncRunLeaseAsync(
        SyncRunStartResult syncRun,
        CancellationToken cancellationToken)
    {
        if (syncRun.Acquired)
            return syncRun;

        while (true)
        {
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
            var claim = _database.TryClaimQueuedSyncRun(syncRun.Id, syncRun.OwnerId);

            if (claim.Acquired)
                return claim;

            if (!claim.Status.Equals("queued", StringComparison.OrdinalIgnoreCase))
                throw new CliException($"Sync run {claim.Id} is no longer queued; status={claim.Status}.", 1);
        }
    }

    private async Task RunWithSyncLeaseHeartbeatAsync(
        SyncRunStartResult syncRun,
        CancellationToken cancellationToken,
        Func<Task> action)
    {
        using var heartbeatCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var heartbeatTask = HeartbeatSyncLeaseAsync(syncRun, heartbeatCancellation.Token);

        try
        {
            await action();
        }
        finally
        {
            heartbeatCancellation.Cancel();
            try
            {
                await heartbeatTask;
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    private async Task HeartbeatSyncLeaseAsync(
        SyncRunStartResult syncRun,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
            if (!_database.HeartbeatSyncLease(syncRun.Id, syncRun.OwnerId))
                throw new OperationCanceledException("Sync lease was lost.");
        }
    }

    private void ThrowIfSyncLeaseLost(SyncRunStartResult syncRun)
    {
        if (!_database.OwnsActiveSyncLease(syncRun.Id, syncRun.OwnerId))
            throw new OperationCanceledException("Sync lease was lost.");
    }

    private void UpdateSyncRunProgressOrThrow(SyncRunStartResult syncRun, int done, int total)
    {
        if (!_database.UpdateSyncRunProgress(syncRun.Id, syncRun.OwnerId, done, total))
            throw new OperationCanceledException("Sync lease was lost.");
    }

    private void UpdateSyncRunPhaseOrThrow(SyncRunStartResult syncRun, string phase, int done, int total)
    {
        if (!_database.UpdateSyncRunPhase(syncRun.Id, syncRun.OwnerId, phase, done, total))
            throw new OperationCanceledException("Sync lease was lost.");
    }

    private static IReadOnlyList<AccountConfig> SelectAccounts(
        AppConfig config,
        IReadOnlyList<string> filters,
        bool enabledOnly)
    {
        var normalizedFilters = filters
            .Where(filter => !string.IsNullOrWhiteSpace(filter))
            .Select(filter => filter.Trim())
            .ToList();

        var accounts = config.Accounts.AsEnumerable();
        if (enabledOnly)
            accounts = accounts.Where(account => account.Enabled);

        if (normalizedFilters.Count > 0)
        {
            accounts = accounts.Where(account => normalizedFilters.Any(filter =>
                account.Id.Equals(filter, StringComparison.OrdinalIgnoreCase)
                || account.EmailAddress.Equals(filter, StringComparison.OrdinalIgnoreCase)
                || account.DisplayName.Equals(filter, StringComparison.OrdinalIgnoreCase)));
        }

        return accounts
            .OrderBy(account => account.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static bool IsConfiguredAccount(AppConfig config, string accountName, string accountEmailAddress) =>
        config.Accounts.Any(account =>
            account.Enabled
            && (account.Id.Equals(accountName, StringComparison.OrdinalIgnoreCase)
                || account.EmailAddress.Equals(accountEmailAddress, StringComparison.OrdinalIgnoreCase)));

    private static JsonObject ToSearchResultJson(EmailSearchResult result) =>
        new()
        {
            ["message_id"] = result.MessageId,
            ["account"] = result.AccountName,
            ["folders"] = ToJsonArray(SplitFolders(result.Folders)),
            ["date"] = StringOrNull(result.Date),
            ["from"] = StringOrNull(result.FromEmail),
            ["from_detail"] = ToPersonJson(result.FromName, result.FromEmail),
            ["subject"] = StringOrNull(result.Subject),
            ["message_hit_count"] = null,
            ["attachment_hit_count"] = 0,
            ["total_hit_count"] = null,
            ["hit_count_mode"] = "not_available",
            ["message_snippets"] = string.IsNullOrWhiteSpace(result.Snippet)
                ? new JsonArray()
                : new JsonArray
                {
                    new JsonObject
                    {
                        ["field"] = "body",
                        ["text"] = result.Snippet
                    }
                },
            ["has_attachments"] = result.HasAttachments,
            ["matching_attachments"] = new JsonArray(),
            ["score"] = result.Score
        };

    private static JsonObject ToReadinessJson(MessageSearchReadiness readiness) =>
        new()
        {
            ["search_ready"] = readiness.SearchReady,
            ["metadata_complete"] = readiness.MetadataComplete,
            ["bodies_complete"] = readiness.BodiesComplete,
            ["message_search_index_complete"] = readiness.MessageSearchIndexComplete,
            ["scope_account_count"] = readiness.ScopeAccountCount,
            ["scope_folder_count"] = readiness.ScopeFolderCount,
            ["metadata_complete_folder_count"] = readiness.MetadataCompleteFolderCount,
            ["metadata_messages"] = readiness.MetadataMessages,
            ["indexed_message_bodies"] = readiness.IndexedMessageBodies,
            ["message_search_docs"] = readiness.MessageSearchDocs,
            ["fts_rows"] = readiness.FtsRows,
            ["pending_message_bodies"] = readiness.PendingMessageBodies,
            ["active_sync_run"] = readiness.ActiveSyncRun is null ? null : ToSyncProgressJson(readiness.ActiveSyncRun)
        };

    private static MessageSearchReadiness EmptyReadiness(SyncRunSnapshot activeSyncRun) =>
        new(
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

    private static JsonObject ToSyncProgressJson(SyncRunSnapshot run) =>
        new()
        {
            ["sync_run_id"] = run.Id,
            ["status"] = run.Status,
            ["phase"] = run.Phase,
            ["account"] = run.AccountName,
            ["folder_filter"] = StringOrNull(run.FolderFilter),
            ["done"] = run.Done,
            ["total"] = run.Total,
            ["percent"] = run.Percent,
            ["elapsed_seconds"] = run.ElapsedSeconds,
            ["estimated_remaining_seconds"] = NumberOrNull(run.EstimatedRemainingSeconds),
            ["estimate_confidence"] = run.EstimateConfidence,
            ["started_at"] = run.StartedAt,
            ["last_progress_at"] = run.LastProgressAt,
            ["completed_at"] = StringOrNull(run.CompletedAt),
            ["last_error"] = StringOrNull(run.LastError)
        };

    private static JsonArray ToFoldersJson(IReadOnlyList<StoredFolder> folders)
    {
        var values = new JsonArray();

        foreach (var folder in folders)
        {
            values.Add(new JsonObject
            {
                ["folder_id"] = folder.Id,
                ["folder"] = folder.Path,
                ["name"] = folder.Name,
                ["role"] = folder.Role,
                ["selectable"] = folder.Selectable,
                ["sync_enabled"] = folder.SyncEnabled,
                ["last_sync_at"] = StringOrNull(folder.LastSyncAt),
                ["last_discovered_at"] = StringOrNull(folder.LastDiscoveredAt)
            });
        }

        return values;
    }

    private static JsonObject ToPersonJson(string name, string email) =>
        new()
        {
            ["name"] = StringOrNull(name),
            ["email"] = StringOrNull(email)
        };

    private static JsonArray ToRecipientArray(IReadOnlyList<MessageRecipient> recipients, string type)
    {
        var values = new JsonArray();

        foreach (var recipient in recipients.Where(recipient => recipient.Type.Equals(type, StringComparison.OrdinalIgnoreCase)))
            values.Add(ToPersonJson(recipient.Name, recipient.Email));

        return values;
    }

    private static McpSyncStatusRequest ParseSyncStatusRequest(JsonElement arguments)
    {
        EnsureArgumentObject(arguments, "email_get_sync_status");
        ValidateAllowedArguments(arguments, "accounts", "include_folders", "_meta");

        var accounts = ReadOptionalStringArray(arguments, "accounts");
        var includeFolders = ReadOptionalBool(arguments, "include_folders", defaultValue: true);
        return new(accounts, includeFolders);
    }

    private static void EnsureArgumentObject(JsonElement arguments, string toolName)
    {
        if (arguments.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            return;

        if (arguments.ValueKind != JsonValueKind.Object)
            throw new JsonRpcError(-32602, $"{toolName} arguments must be an object.");
    }

    private static void ValidateAllowedArguments(JsonElement arguments, params string[] allowed)
    {
        if (arguments.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            return;

        var allowedSet = allowed.ToHashSet(StringComparer.Ordinal);
        foreach (var property in arguments.EnumerateObject())
        {
            if (!allowedSet.Contains(property.Name))
                throw new JsonRpcError(-32602, $"Unknown argument: {property.Name}");
        }
    }

    private static IReadOnlyList<string> ReadOptionalStringArray(JsonElement element, string name)
    {
        if (element.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null
            || !element.TryGetProperty(name, out var value)
            || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return [];

        if (value.ValueKind != JsonValueKind.Array)
            throw new JsonRpcError(-32602, $"{name} must be an array of strings, null, or omitted.");

        var values = new List<string>();
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String)
                throw new JsonRpcError(-32602, $"{name} must contain only strings.");

            var text = item.GetString();
            if (!string.IsNullOrWhiteSpace(text))
                values.Add(text.Trim());
        }

        return values;
    }

    private static string ReadOptionalString(JsonElement element, string name)
    {
        if (element.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null
            || !element.TryGetProperty(name, out var value)
            || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return null;

        if (value.ValueKind != JsonValueKind.String)
            throw new JsonRpcError(-32602, $"{name} must be a string or null.");

        var text = value.GetString();
        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }

    private static bool ReadOptionalBool(JsonElement element, string name, bool defaultValue)
    {
        if (element.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null
            || !element.TryGetProperty(name, out var value)
            || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return defaultValue;

        if (value.ValueKind is JsonValueKind.True)
            return true;

        if (value.ValueKind is JsonValueKind.False)
            return false;

        throw new JsonRpcError(-32602, $"{name} must be a boolean.");
    }

    private static bool? ReadOptionalNullableBool(JsonElement element, string name)
    {
        if (element.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null
            || !element.TryGetProperty(name, out var value)
            || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return null;

        if (value.ValueKind is JsonValueKind.True)
            return true;

        if (value.ValueKind is JsonValueKind.False)
            return false;

        throw new JsonRpcError(-32602, $"{name} must be a boolean or null.");
    }

    private static int ReadOptionalInt(JsonElement element, string name, int defaultValue, int min, int max)
    {
        if (element.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null
            || !element.TryGetProperty(name, out var value)
            || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return defaultValue;

        return ReadIntValue(value, name, min, max);
    }

    private static int? ReadOptionalNullableInt(JsonElement element, string name, int min, int max)
    {
        if (element.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null
            || !element.TryGetProperty(name, out var value)
            || value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return null;

        return ReadIntValue(value, name, min, max);
    }

    private static int ReadRequiredInt(JsonElement element, string name, int min, int max)
    {
        if (element.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null
            || !element.TryGetProperty(name, out var value))
            throw new JsonRpcError(-32602, $"{name} is required.");

        return ReadIntValue(value, name, min, max);
    }

    private static int ReadIntValue(JsonElement value, string name, int min, int max)
    {
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var parsed))
            throw new JsonRpcError(-32602, $"{name} must be an integer.");

        if (parsed < min || parsed > max)
            throw new JsonRpcError(-32602, $"{name} must be between {min} and {max}.");

        return parsed;
    }

    private static string ReadRequiredString(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value)
            || value.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(value.GetString()))
            throw new JsonRpcError(-32602, $"{name} is required.");

        return value.GetString();
    }

    private static string TryGetString(JsonElement element, params string[] path)
    {
        var current = element;

        foreach (var item in path)
        {
            if (current.ValueKind != JsonValueKind.Object
                || !current.TryGetProperty(item, out current))
                return null;
        }

        return current.ValueKind == JsonValueKind.String ? current.GetString() : null;
    }

    private static JsonNode ReadId(JsonElement message)
    {
        if (!message.TryGetProperty("id", out var id))
            return null;

        return id.ValueKind switch
        {
            JsonValueKind.String => JsonValue.Create(id.GetString()),
            JsonValueKind.Number when id.TryGetInt64(out var number) => JsonValue.Create(number),
            JsonValueKind.Null => null,
            _ => JsonValue.Create(id.GetRawText())
        };
    }

    private async Task WriteResultAsync(JsonNode id, JsonNode result, CancellationToken cancellationToken)
    {
        var response = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["result"] = result
        };

        await WriteMessageAsync(response, cancellationToken);
    }

    private async Task WriteErrorAsync(
        JsonNode id,
        int code,
        string message,
        CancellationToken cancellationToken)
    {
        var response = new JsonObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id,
            ["error"] = new JsonObject
            {
                ["code"] = code,
                ["message"] = message
            }
        };

        await WriteMessageAsync(response, cancellationToken);
    }

    private async Task WriteMessageAsync(JsonObject message, CancellationToken cancellationToken)
    {
        var text = message.ToJsonString(JsonOptions);
        await _output.WriteLineAsync(text.AsMemory(), cancellationToken);
        await _output.FlushAsync(cancellationToken);
    }

    private static JsonObject ToolResult(JsonObject payload, bool isError = false)
    {
        var text = payload.ToJsonString(JsonOptions);
        return new()
        {
            ["content"] = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "text",
                    ["text"] = text
                }
            },
            ["structuredContent"] = payload.DeepClone(),
            ["isError"] = isError
        };
    }

    private static JsonObject ToolDefinition(
        string name,
        string title,
        string description,
        JsonObject inputSchema,
        bool readOnly,
        bool idempotent,
        bool openWorld = false) =>
        new()
        {
            ["name"] = name,
            ["title"] = title,
            ["description"] = description,
            ["inputSchema"] = inputSchema,
            ["annotations"] = new JsonObject
            {
                ["readOnlyHint"] = readOnly,
                ["destructiveHint"] = false,
                ["idempotentHint"] = idempotent,
                ["openWorldHint"] = openWorld
            }
        };

    private static JsonObject ObjectSchema(JsonObject properties, params string[] required)
    {
        var schema = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["additionalProperties"] = false
        };

        if (required.Length > 0)
            schema["required"] = ToJsonArray(required);

        return schema;
    }

    private static JsonObject StringSchema(string description) =>
        new()
        {
            ["type"] = "string",
            ["description"] = description
        };

    private static JsonObject NullableStringSchema(string description) =>
        new()
        {
            ["type"] = new JsonArray("string", "null"),
            ["description"] = description
        };

    private static JsonObject BoolSchema(string description, bool defaultValue) =>
        new()
        {
            ["type"] = "boolean",
            ["description"] = description,
            ["default"] = defaultValue
        };

    private static JsonObject NullableBoolSchema(string description) =>
        new()
        {
            ["type"] = new JsonArray("boolean", "null"),
            ["description"] = description
        };

    private static JsonObject StringArraySchema(string description) =>
        new()
        {
            ["type"] = new JsonArray("array", "null"),
            ["description"] = description,
            ["items"] = new JsonObject
            {
                ["type"] = "string"
            }
        };

    private static JsonObject IntSchema(string description, int minimum, int maximum, int? defaultValue)
    {
        var schema = new JsonObject
        {
            ["type"] = "integer",
            ["description"] = description,
            ["minimum"] = minimum,
            ["maximum"] = maximum
        };

        if (defaultValue is int value)
            schema["default"] = value;

        return schema;
    }

    private static string SummarizeSyncStatus(JsonObject payload)
    {
        var accounts = payload["accounts"]?.AsArray();
        if (accounts is null)
            return "accounts=0";

        var ready = accounts
            .OfType<JsonObject>()
            .Count(account => account["search_ready"]?.GetValue<bool>() == true);

        return $"accounts={accounts.Count}, search_ready={ready}";
    }

    private static string FormatAccountFilters(IReadOnlyList<string> accounts) =>
        accounts.Count == 0 ? "all" : string.Join(",", accounts);

    private static string FormatOptional(string value) =>
        string.IsNullOrWhiteSpace(value) ? "all" : value;

    private static bool IsActiveForAccount(SyncRunSnapshot run, AccountConfig account)
    {
        if (run is null)
            return false;

        return run.AccountName.Equals("multiple", StringComparison.OrdinalIgnoreCase)
            || run.AccountName.Equals(account.Id, StringComparison.OrdinalIgnoreCase)
            || run.AccountName.Equals(account.EmailAddress, StringComparison.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<string> SplitFolders(string folders)
    {
        if (string.IsNullOrWhiteSpace(folders))
            return [];

        return folders
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(folder => !string.IsNullOrWhiteSpace(folder))
            .ToList();
    }

    private static JsonArray ToJsonArray(IEnumerable<string> values)
    {
        var array = new JsonArray();
        foreach (var value in values)
            array.Add(value);

        return array;
    }

    private static JsonNode StringOrNull(string value) =>
        value is null ? null : JsonValue.Create(value);

    private static JsonNode NumberOrNull(int? value) =>
        value is null ? null : JsonValue.Create(value.Value);

    private sealed record ToolExecution(
        JsonObject Payload,
        string ActionType,
        string ArgumentsSummary,
        string ResultSummary,
        IReadOnlyList<int> AffectedMessageIds = null,
        bool IsError = false);

    private sealed record McpSyncStatusRequest(
        IReadOnlyList<string> Accounts,
        bool IncludeFolders);

    private sealed record McpSyncNowRequest(
        IReadOnlyList<string> Accounts,
        string Folder,
        bool Full,
        int? SinceDays,
        int MaxPerFolder,
        bool WaitForCompletion);

    private sealed record SyncAccountWork(
        AccountConfig Account,
        int DatabaseAccountId,
        string Password);

    private sealed class JsonRpcError : Exception
    {
        public JsonRpcError(int code, string message)
            : base(message)
        {
            Code = code;
        }

        public int Code { get; }
    }
}
