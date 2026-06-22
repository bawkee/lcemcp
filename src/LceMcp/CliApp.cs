using System.Text.RegularExpressions;
namespace LceMcp;

internal static class CliApp
{
    public static async Task<int> RunAsync(string[] args, CancellationToken cancellationToken)
    {
        if (args.Length == 0 || IsHelp(args[0]))
        {
            PrintHelp();
            return 0;
        }

        var command = args[0].ToLowerInvariant();
        var options = CommandOptions.Parse(args.Skip(1));
        var paths = AppPaths.FromEnvironment();
        var configStore = new ConfigStore(paths);
        var credentialStore = new WindowsCredentialStore();
        var database = new EmailDatabase(paths);

        return command switch
        {
            "setup-yahoo" => await SetupYahooAsync(configStore, credentialStore, database, options, cancellationToken),
            "status" => Status(configStore, database),
            "accounts" => ListAccounts(configStore, credentialStore),
            "discover-folders" => await DiscoverFoldersAsync(configStore, credentialStore, database, options, cancellationToken),
            "folders" => ListFolders(database, options),
            "set-folder-sync" => SetFolderSync(database, options),
            "sync" => await SyncAsync(configStore, credentialStore, database, options, cancellationToken),
            "sync-bodies" => await SyncBodiesAsync(configStore, credentialStore, database, options, cancellationToken),
            "search" => Search(database, options),
            "serve" => await McpStdioServer.RunAsync(configStore, database, cancellationToken),
            "credential-test" => CredentialTest(configStore, credentialStore, options),
            "credential-update" => CredentialUpdate(configStore, credentialStore, options),
            "credential-delete" => CredentialDelete(configStore, credentialStore, options),
            "imap-test" => await ImapTestAsync(configStore, credentialStore, options, cancellationToken),
            "help" => PrintHelpAndReturn(),
            _ => throw new CliException($"Unknown command '{args[0]}'. Run 'lcemcp help'.", 2)
        };
    }

    private static async Task<int> SetupYahooAsync(
        ConfigStore configStore,
        WindowsCredentialStore credentialStore,
        EmailDatabase database,
        CommandOptions options,
        CancellationToken cancellationToken)
    {
        var email = options.GetRequired("--email").Trim();
        var displayName = options.Get("--name") ?? "Yahoo";
        var username = options.Get("--username") ?? email;
        var historyDays = options.GetInt("--history-days", 90);

        if (historyDays < 1)
            throw new CliException("--history-days must be at least 1 for this first probe.", 2);

        var config = configStore.Load();
        var existing = config.FindAccountByEmail(email);
        var requestedId = options.Get("--id");
        var accountId = !string.IsNullOrWhiteSpace(requestedId)
            ? Slugify(requestedId)
            : existing?.Id ?? NextAvailableId(config, "yahoo", email);

        var credentialRef = WindowsCredentialStore.BuildImapTarget(accountId);
        var account = new AccountConfig
        {
            Id = accountId,
            DisplayName = displayName.Trim(),
            EmailAddress = email,
            Provider = "yahoo",
            Username = username.Trim(),
            ImapHost = YahooPreset.ImapHost,
            ImapPort = YahooPreset.ImapPort,
            ImapSecurity = "ssl",
            HistoryDays = historyDays,
            AttachmentPolicy = "metadata_only",
            CredentialRef = credentialRef,
            Enabled = true
        };

        if (!options.Has("--skip-password"))
        {
            Console.WriteLine("Yahoo usually requires an app password for third-party IMAP clients.");
            var password = options.Has("--password-stdin")
                ? ReadPasswordFromStdin()
                : ConsoleSecretReader.ReadSecret("Password/app password: ");

            if (string.IsNullOrWhiteSpace(password))
                throw new CliException("No password was provided; config was not changed.", 2);

            credentialStore.Write(credentialRef, username, password);
            Console.WriteLine($"Stored IMAP credential in Windows Credential Manager: {credentialRef}");
        }

        config.UpsertAccount(account);
        configStore.Save(config);
        var databaseAccountId = database.UpsertConfiguredAccount(account);
        cancellationToken.ThrowIfCancellationRequested();

        Console.WriteLine($"Saved account '{account.Id}' to {configStore.ConfigPath}");
        Console.WriteLine($"Persisted account metadata as database account {databaseAccountId} in {database.DatabasePath}");
        Console.WriteLine("Next test:");
        Console.WriteLine($"  dotnet run --project src/LceMcp -- imap-test --account {account.Id} --limit 5");
        return 0;
    }

    private static int Status(ConfigStore configStore, EmailDatabase database)
    {
        var configExists = File.Exists(configStore.ConfigPath);
        var config = configStore.Load();
        var databaseStatus = database.GetStatus();

        Console.WriteLine($"Config: {configStore.ConfigPath} ({(configExists ? "present" : "missing")})");
        Console.WriteLine($"Database: {database.DatabasePath} ({FormatInitializationKind(databaseStatus.InitializationKind)})");
        Console.WriteLine($"Configured accounts: {config.Accounts.Count}");

        var configErrors = config.Accounts
            .SelectMany(account => AccountConfigValidator.ValidateForImap(account)
                .Select(error => $"{AccountLabel(account)}: {error}"))
            .ToList();

        if (configErrors.Count == 0)
        {
            Console.WriteLine("Config validation: ok");
        }
        else
        {
            Console.WriteLine($"Config validation: {configErrors.Count} issue(s)");
            foreach (var error in configErrors)
                Console.WriteLine($"  {error}");
        }

        Console.WriteLine($"Database schema version: {databaseStatus.SchemaVersion} / target {databaseStatus.TargetSchemaVersion}");
        Console.WriteLine($"Database accounts: {databaseStatus.AccountCount}");
        Console.WriteLine($"Database folders: {databaseStatus.FolderCount}");
        Console.WriteLine($"Database messages: {databaseStatus.MessageCount}");
        Console.WriteLine($"Database message locations: {databaseStatus.MessageLocationCount}");
        Console.WriteLine($"Database message bodies: {databaseStatus.MessageBodyCount}");
        Console.WriteLine($"Database message search docs: {databaseStatus.MessageSearchDocCount}");
        Console.WriteLine($"Last sync state: {FormatSyncState(databaseStatus.LastSyncState)}");
        PrintMessageSearchReadiness(database.GetMessageSearchReadiness(new(
            AccountFilters: [],
            FromEmail: null,
            FolderRoles: [],
            HasAttachment: null)));

        return 0;
    }

    private static async Task<int> DiscoverFoldersAsync(
        ConfigStore configStore,
        WindowsCredentialStore credentialStore,
        EmailDatabase database,
        CommandOptions options,
        CancellationToken cancellationToken)
    {
        var account = ResolveAccount(configStore.Load(), options.Get("--account"));

        if (!account.Enabled)
            throw new CliException($"Account '{account.Id}' is disabled.", 2);

        AccountConfigValidator.ThrowIfInvalidForImap(account);

        var password = credentialStore.Read(account.CredentialRef);
        if (password is null)
            throw new CliException($"Credential not found: {account.CredentialRef}", 3);

        var databaseAccountId = database.UpsertConfiguredAccount(account);

        Console.WriteLine($"Discovering folders for '{account.Id}' via {account.ImapHost}:{account.ImapPort}/{account.ImapSecurity}...");
        var result = await new ImapFolderDiscovery().DiscoverAsync(account, password, cancellationToken);
        var persistedCount = database.UpsertFolders(databaseAccountId, result.Folders);

        Console.WriteLine($"Capabilities: {result.Capabilities}");
        Console.WriteLine($"Discovered folders: {result.Folders.Count}");
        Console.WriteLine($"Persisted folders: {persistedCount}");
        PrintDiscoveredFolderSample(result.Folders);

        var statusWarnings = result.Folders
            .Where(folder => !string.IsNullOrWhiteSpace(folder.StatusError))
            .ToList();

        if (statusWarnings.Count > 0)
        {
            Console.WriteLine($"Folder status warnings: {statusWarnings.Count}");
            foreach (var folder in statusWarnings.Take(10))
                Console.WriteLine($"  {folder.FullName}: {folder.StatusError}");
        }

        Console.WriteLine($"Local folder cache:");
        Console.WriteLine($"  dotnet run --project src/LceMcp -- folders --account {account.Id}");
        return 0;
    }

    private static int ListFolders(EmailDatabase database, CommandOptions options)
    {
        var accountFilter = options.Get("--account");
        var folders = database.ReadFolders(accountFilter);

        Console.WriteLine($"Database: {database.DatabasePath}");

        if (folders.Count == 0)
        {
            Console.WriteLine(string.IsNullOrWhiteSpace(accountFilter)
                ? "No folders are stored yet. Run 'discover-folders' first."
                : $"No folders are stored for account '{accountFilter}'. Run 'discover-folders --account {accountFilter}' first.");
            return 0;
        }

        foreach (var folder in folders)
        {
            Console.WriteLine($"{folder.AccountName}  {folder.Path}  role={folder.Role}  selectable={folder.Selectable.ToString().ToLowerInvariant()}  sync={folder.SyncEnabled.ToString().ToLowerInvariant()}");
            Console.WriteLine($"  id={folder.Id}  name={folder.Name}  delimiter={FormatOptional(folder.Delimiter)}  uidvalidity={FormatOptional(folder.UidValidity)}  messages={FormatOptional(folder.MessageCount)}  recent={FormatOptional(folder.RecentCount)}");
            Console.WriteLine($"  attrs={FormatOptional(folder.Attributes)}  discovered={FormatOptional(folder.LastDiscoveredAt)}  last_sync={FormatOptional(folder.LastSyncAt)}");
        }

        return 0;
    }

    private static int SetFolderSync(EmailDatabase database, CommandOptions options)
    {
        var account = options.GetRequired("--account");
        var folder = options.GetRequired("--folder");
        var enabled = ParseNullableBool(options.GetRequired("--enabled"), "--enabled")
            ?? throw new CliException("--enabled must be true or false.", 2);
        var changed = database.SetFolderSyncEnabled(account, folder, enabled);

        if (changed == 0)
            throw new CliException($"No folder matched account '{account}' and folder '{folder}'.", 2);

        Console.WriteLine($"Updated folder sync setting: account={account} folder={folder} sync_enabled={enabled.ToString().ToLowerInvariant()}");
        return 0;
    }

    private static async Task<int> SyncAsync(
        ConfigStore configStore,
        WindowsCredentialStore credentialStore,
        EmailDatabase database,
        CommandOptions options,
        CancellationToken cancellationToken)
    {
        var config = configStore.Load();
        var accounts = ResolveAccountsForSync(config, options.Get("--account"));
        var folderFilter = options.Get("--folder");
        var maxPerFolder = options.GetInt("--max-per-folder", 200);
        var batchSize = options.GetInt("--batch-size", 50);

        if (maxPerFolder < 0)
            throw new CliException("--max-per-folder must be 0 or greater. Use 0 for no per-folder cap.", 2);

        if (batchSize < 1 || batchSize > 500)
            throw new CliException("--batch-size must be between 1 and 500.", 2);

        foreach (var account in accounts)
        {
            if (!account.Enabled)
                throw new CliException($"Account '{account.Id}' is disabled.", 2);

            AccountConfigValidator.ThrowIfInvalidForImap(account);

            var password = credentialStore.Read(account.CredentialRef);
            if (password is null)
                throw new CliException($"Credential not found: {account.CredentialRef}", 3);

            var requestedSinceDays = options.GetInt("--since-days", account.HistoryDays);
            if (requestedSinceDays < 0)
                throw new CliException("--since-days must be 0 or greater.", 2);

            var databaseAccountId = database.UpsertConfiguredAccount(account);
            var folders = database.ReadSyncFolders(account.Id, folderFilter);

            if (folders.Count == 0 && database.ReadFolders(account.Id).Count == 0)
            {
                Console.WriteLine($"No syncable folders are cached for '{account.Id}'; discovering folders first...");
                var discovery = await new ImapFolderDiscovery().DiscoverAsync(account, password, cancellationToken);
                database.UpsertFolders(databaseAccountId, discovery.Folders);
                folders = database.ReadSyncFolders(account.Id, folderFilter);
            }

            if (folders.Count == 0)
            {
                var suffix = string.IsNullOrWhiteSpace(folderFilter)
                    ? ""
                    : $" matching '{folderFilter}'";

                throw new CliException($"No syncable folders{suffix} are stored for account '{account.Id}'. Run 'discover-folders --account {account.Id}' first.", 2);
            }

            var syncWindow = database.PlanMetadataSyncWindow(databaseAccountId, folders, requestedSinceDays);

            Console.WriteLine($"Syncing metadata for '{account.Id}' from {folders.Count} folder(s), requested_since_days={syncWindow.RequestedSinceDays}, effective_since_days={syncWindow.EffectiveSinceDays}, auto_expanded_for_gap={syncWindow.AutoExpandedForGap.ToString().ToLowerInvariant()}, max_per_folder={FormatMaxPerFolder(maxPerFolder)}, batch_size={batchSize}...");

            var syncRun = database.StartOrQueueSyncRun(
                databaseAccountId,
                account.Id,
                folderFilter,
                "syncing_metadata",
                folders.Count,
                syncWindow.RequestedSinceDays,
                syncWindow.EffectiveSinceDays,
                syncWindow.AutoExpandedForGap);
            Console.WriteLine($"Sync run: {syncRun.Id} status={syncRun.Status}");

            try
            {
                syncRun = await WaitForSyncRunLeaseAsync(database, syncRun, cancellationToken);
                PrintSyncProgress(syncRun.ActiveRun);

                var result = await RunWithSyncLeaseHeartbeatAsync(database, syncRun, cancellationToken, async () =>
                {
                    var syncResult = await new ImapMetadataSync(database).SyncAccountAsync(
                        account,
                        password,
                        databaseAccountId,
                        folders,
                        syncWindow.RequestedSinceDays,
                        syncWindow.EffectiveSinceDaysByFolder,
                        maxPerFolder,
                        batchSize,
                        cancellationToken,
                        (done, total) => UpdateSyncRunProgressOrThrow(database, syncRun, done, total),
                        () => ThrowIfSyncLeaseLost(database, syncRun));

                    var failed = syncResult.Folders.Where(folder => !folder.Succeeded).ToList();
                    var completed = database.CompleteSyncRun(
                        syncRun.Id,
                        syncRun.OwnerId,
                        succeeded: failed.Count == 0,
                        done: syncResult.Folders.Sum(folder => folder.FetchedCount),
                        total: syncResult.Folders.Sum(folder => folder.SelectedCount),
                        lastError: failed.Count == 0 ? null : string.Join("; ", failed.Select(folder => $"{folder.FolderPath}: {folder.Error}")));
                    if (!completed)
                        throw new OperationCanceledException("Sync lease was lost.");

                    return syncResult;
                }, PrintSyncProgress);

                PrintMetadataSyncResult(result);
            }
            catch (OperationCanceledException)
            {
                database.CancelSyncRun(syncRun.Id, syncRun.OwnerId, done: 0, total: folders.Count);
                throw;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                database.CompleteSyncRun(syncRun.Id, syncRun.OwnerId, succeeded: false, done: 0, total: folders.Count, lastError: ex.Message);
                throw;
            }
        }

        return 0;
    }

    private static async Task<int> SyncBodiesAsync(
        ConfigStore configStore,
        WindowsCredentialStore credentialStore,
        EmailDatabase database,
        CommandOptions options,
        CancellationToken cancellationToken)
    {
        var config = configStore.Load();
        var accounts = ResolveAccountsForSync(config, options.Get("--account"));
        var folderFilter = options.Get("--folder");
        var maxPerFolder = options.GetInt("--max-per-folder", 50);
        var batchSize = options.GetInt("--batch-size", 10);

        if (maxPerFolder < 0)
            throw new CliException("--max-per-folder must be 0 or greater. Use 0 for no per-folder cap.", 2);

        if (batchSize < 1 || batchSize > 100)
            throw new CliException("--batch-size must be between 1 and 100.", 2);

        foreach (var account in accounts)
        {
            if (!account.Enabled)
                throw new CliException($"Account '{account.Id}' is disabled.", 2);

            AccountConfigValidator.ThrowIfInvalidForImap(account);

            var password = credentialStore.Read(account.CredentialRef);
            if (password is null)
                throw new CliException($"Credential not found: {account.CredentialRef}", 3);

            var databaseAccountId = database.UpsertConfiguredAccount(account);

            Console.WriteLine($"Syncing bodies for '{account.Id}', max_per_folder={FormatMaxPerFolder(maxPerFolder)}, batch_size={batchSize}...");

            var pendingTargetCount = database.ReadPendingBodySyncTargets(account.Id, folderFilter, maxPerFolder).Count;
            var syncRun = database.StartOrQueueSyncRun(databaseAccountId, account.Id, folderFilter, "syncing_bodies", pendingTargetCount);
            Console.WriteLine($"Sync run: {syncRun.Id} status={syncRun.Status}");

            try
            {
                syncRun = await WaitForSyncRunLeaseAsync(database, syncRun, cancellationToken);
                PrintSyncProgress(syncRun.ActiveRun);

                var result = await RunWithSyncLeaseHeartbeatAsync(database, syncRun, cancellationToken, async () =>
                {
                    var syncResult = await new ImapBodySync(database).SyncAccountAsync(
                        account,
                        password,
                        folderFilter,
                        maxPerFolder,
                        batchSize,
                        cancellationToken,
                        (done, total) => UpdateSyncRunProgressOrThrow(database, syncRun, done, total),
                        () => ThrowIfSyncLeaseLost(database, syncRun));

                    var failed = syncResult.Folders.Where(folder => !folder.Succeeded).ToList();
                    var completed = database.CompleteSyncRun(
                        syncRun.Id,
                        syncRun.OwnerId,
                        succeeded: failed.Count == 0,
                        done: syncResult.Folders.Sum(folder => folder.PersistedCount),
                        total: syncResult.Folders.Sum(folder => folder.SelectedCount),
                        lastError: failed.Count == 0 ? null : string.Join("; ", failed.Select(folder => $"{folder.FolderPath}: {folder.Error}")));
                    if (!completed)
                        throw new OperationCanceledException("Sync lease was lost.");

                    return syncResult;
                }, PrintSyncProgress);

                PrintBodySyncResult(result);
            }
            catch (OperationCanceledException)
            {
                database.CancelSyncRun(syncRun.Id, syncRun.OwnerId, done: 0, total: pendingTargetCount);
                throw;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                database.CompleteSyncRun(syncRun.Id, syncRun.OwnerId, succeeded: false, done: 0, total: pendingTargetCount, lastError: ex.Message);
                throw;
            }
        }

        return 0;
    }

    private static int Search(EmailDatabase database, CommandOptions options)
    {
        var query = options.GetRequired("--query").Trim();
        var dateFrom = EmailSearchDateParser.NormalizeLowerBound(options.Get("--date-from"));
        var dateTo = EmailSearchDateParser.NormalizeUpperBound(options.Get("--date-to"));
        EmailSearchDateParser.ValidateRange(dateFrom, dateTo);

        var request = new EmailSearchRequest(
            Query: query,
            AccountFilters: SplitOption(options.Get("--account")),
            FromEmail: options.Get("--from"),
            FolderRoles: SplitOption(options.Get("--folder-role")),
            HasAttachment: ParseNullableBool(options.Get("--has-attachment"), "--has-attachment"),
            Limit: options.GetInt("--limit", 20),
            SnippetChars: options.GetInt("--snippet-chars", 1024),
            AllowPartial: options.Has("--allow-partial"),
            ToEmail: options.Get("--to"),
            DateFrom: dateFrom,
            DateTo: dateTo,
            Cursor: options.Get("--cursor"));

        if (request.Limit < 1 || request.Limit > 100)
            throw new CliException("--limit must be between 1 and 100.", 2);

        if (request.SnippetChars < 160 || request.SnippetChars > 4096)
            throw new CliException("--snippet-chars must be between 160 and 4096.", 2);

        Console.WriteLine($"Database: {database.DatabasePath}");

        FtsQueryBuilder.Build(request.Query);
        EmailSearchCursorCodec.Decode(request.Cursor);

        var readiness = database.GetMessageSearchReadiness(new(
            AccountFilters: request.AccountFilters,
            FromEmail: request.FromEmail,
            FolderRoles: request.FolderRoles,
            HasAttachment: request.HasAttachment,
            ToEmail: request.ToEmail,
            DateFrom: request.DateFrom,
            DateTo: request.DateTo));

        if (!readiness.SearchReady && !request.AllowPartial)
        {
            Console.WriteLine("Search status: not_synced");
            PrintMessageSearchReadiness(readiness);
            Console.WriteLine("Search results: not run");
            return 0;
        }

        if (!readiness.SearchReady)
        {
            Console.WriteLine("Search status: partial");
            PrintMessageSearchReadiness(readiness);
        }
        else
        {
            Console.WriteLine("Search status: ready");
        }

        var rawResults = database.SearchMessages(request with { Limit = Math.Min(request.Limit + 1, 101) });
        var hasMore = rawResults.Count > request.Limit;
        var results = rawResults.Take(request.Limit).ToList();
        var nextCursor = hasMore ? results.LastOrDefault()?.Cursor : null;

        Console.WriteLine($"Search results: {results.Count}");
        Console.WriteLine($"Has more: {hasMore.ToString().ToLowerInvariant()}");
        if (!string.IsNullOrWhiteSpace(nextCursor))
            Console.WriteLine($"Next cursor: {nextCursor}");

        foreach (var result in results)
            PrintSearchResult(result);

        return 0;
    }

    private static int ListAccounts(ConfigStore configStore, WindowsCredentialStore credentialStore)
    {
        var config = configStore.Load();

        Console.WriteLine($"Config: {configStore.ConfigPath}");

        if (config.Accounts.Count == 0)
        {
            Console.WriteLine("No accounts configured.");
            return 0;
        }

        foreach (var account in config.Accounts)
        {
            var credentialStatus = CredentialStatus(credentialStore, account.CredentialRef);
            var validationErrors = AccountConfigValidator.ValidateForImap(account);
            var validationStatus = validationErrors.Count == 0 ? "valid" : "invalid";
            Console.WriteLine($"{account.Id}  {account.EmailAddress}  provider={account.Provider}  enabled={account.Enabled}  credential={credentialStatus}");
            Console.WriteLine($"  imap={account.ImapHost}:{account.ImapPort}/{account.ImapSecurity}  user={account.Username}  history_days={account.HistoryDays}  config={validationStatus}");

            foreach (var error in validationErrors)
                Console.WriteLine($"  config-error: {error}");
        }

        return 0;
    }

    private static int CredentialTest(ConfigStore configStore, WindowsCredentialStore credentialStore, CommandOptions options)
    {
        var account = ResolveAccount(configStore.Load(), CredentialAccountOption(options));

        if (string.IsNullOrWhiteSpace(account.CredentialRef))
            throw new CliException($"Account '{account.Id}' has no credential_ref in config.", 2);

        var exists = credentialStore.Exists(account.CredentialRef);
        Console.WriteLine(exists
            ? $"Credential exists: {account.CredentialRef}"
            : $"Credential not found: {account.CredentialRef}");

        return exists ? 0 : 3;
    }

    private static int CredentialUpdate(ConfigStore configStore, WindowsCredentialStore credentialStore, CommandOptions options)
    {
        var account = ResolveAccount(configStore.Load(), CredentialAccountOption(options));

        if (string.IsNullOrWhiteSpace(account.CredentialRef))
            throw new CliException($"Account '{account.Id}' has no credential_ref in config.", 2);

        if (string.IsNullOrWhiteSpace(account.Username))
            throw new CliException($"Account '{account.Id}' has no username in config.", 2);

        Console.WriteLine($"Updating credential for account '{account.Id}' ({account.EmailAddress}).");
        var password = options.Has("--password-stdin")
            ? ReadPasswordFromStdin()
            : ConsoleSecretReader.ReadSecret("New password/app password: ");

        if (string.IsNullOrWhiteSpace(password))
            throw new CliException("No password was provided; credential was not changed.", 2);

        credentialStore.Write(account.CredentialRef, account.Username, password);
        Console.WriteLine($"Updated IMAP credential in Windows Credential Manager: {account.CredentialRef}");
        return 0;
    }

    private static int CredentialDelete(ConfigStore configStore, WindowsCredentialStore credentialStore, CommandOptions options)
    {
        var account = ResolveAccount(configStore.Load(), CredentialAccountOption(options));

        if (string.IsNullOrWhiteSpace(account.CredentialRef))
            throw new CliException($"Account '{account.Id}' has no credential_ref in config.", 2);

        var deleted = credentialStore.Delete(account.CredentialRef);
        Console.WriteLine(deleted
            ? $"Deleted credential: {account.CredentialRef}"
            : $"Credential was already absent: {account.CredentialRef}");

        return 0;
    }

    private static string CredentialAccountOption(CommandOptions options) =>
        options.Get("--account") ?? options.Get("--id");

    private static async Task<int> ImapTestAsync(
        ConfigStore configStore,
        WindowsCredentialStore credentialStore,
        CommandOptions options,
        CancellationToken cancellationToken)
    {
        var account = ResolveAccount(configStore.Load(), options.Get("--account"));

        if (!account.Enabled)
            throw new CliException($"Account '{account.Id}' is disabled.", 2);

        AccountConfigValidator.ThrowIfInvalidForImap(account);

        var password = credentialStore.Read(account.CredentialRef);
        if (password is null)
            throw new CliException($"Credential not found: {account.CredentialRef}", 3);

        var probeOptions = new ImapProbeOptions
        {
            Folder = options.Get("--folder") ?? "INBOX",
            Query = options.Get("--query"),
            Limit = options.GetInt("--limit", 5),
            SinceDays = options.GetInt("--since-days", 30),
            FetchFirstBody = options.Has("--fetch-first-body"),
            BodyChars = options.GetInt("--body-chars", 1200)
        };

        await new ImapProbe().RunAsync(account, password, probeOptions, cancellationToken);
        return 0;
    }

    private static string CredentialStatus(WindowsCredentialStore credentialStore, string target)
    {
        if (string.IsNullOrWhiteSpace(target))
            return "not-configured";

        try
        {
            return credentialStore.Exists(target) ? "present" : "missing";
        }
        catch (PlatformNotSupportedException)
        {
            return "unsupported-platform";
        }
    }

    private static AccountConfig ResolveAccount(AppConfig config, string requestedAccount)
    {
        if (!string.IsNullOrWhiteSpace(requestedAccount))
        {
            var account = config.FindAccount(requestedAccount);
            if (account is not null)
                return account;

            throw new CliException($"Account not found: {requestedAccount}", 2);
        }

        if (config.Accounts.Count == 1)
            return config.Accounts[0];

        if (config.Accounts.Count == 0)
            throw new CliException("No accounts configured. Run 'setup-yahoo' first.", 2);

        throw new CliException("Multiple accounts are configured; pass --account <id-or-email>.", 2);
    }

    private static IReadOnlyList<AccountConfig> ResolveAccountsForSync(AppConfig config, string requestedAccount)
    {
        if (!string.IsNullOrWhiteSpace(requestedAccount))
            return [ResolveAccount(config, requestedAccount)];

        var accounts = config.Accounts
            .Where(account => account.Enabled)
            .OrderBy(account => account.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (accounts.Count == 0)
            throw new CliException("No enabled accounts configured. Run 'setup-yahoo' first.", 2);

        return accounts;
    }

    private static string NextAvailableId(AppConfig config, string preferredId, string email)
    {
        if (config.FindAccount(preferredId) is null)
            return preferredId;

        var emailSlug = Slugify(email);
        if (config.FindAccount(emailSlug) is null)
            return emailSlug;

        for (var i = 2; ; i++)
        {
            var candidate = $"{emailSlug}-{i}";
            if (config.FindAccount(candidate) is null)
                return candidate;
        }
    }

    private static string Slugify(string value)
    {
        var slug = Regex.Replace(value.Trim().ToLowerInvariant(), @"[^a-z0-9]+", "-").Trim('-');
        return string.IsNullOrWhiteSpace(slug) ? "account" : slug;
    }

    private static string ReadPasswordFromStdin()
    {
        var value = Console.In.ReadToEnd();
        return value.TrimEnd('\r', '\n');
    }

    private static bool IsHelp(string arg) =>
        arg.Equals("-h", StringComparison.OrdinalIgnoreCase)
        || arg.Equals("--help", StringComparison.OrdinalIgnoreCase)
        || arg.Equals("help", StringComparison.OrdinalIgnoreCase);

    private static int PrintHelpAndReturn()
    {
        PrintHelp();
        return 0;
    }

    private static string AccountLabel(AccountConfig account)
    {
        if (!string.IsNullOrWhiteSpace(account.Id))
            return account.Id;

        if (!string.IsNullOrWhiteSpace(account.EmailAddress))
            return account.EmailAddress;

        return "(unnamed account)";
    }

    private static void PrintDiscoveredFolderSample(IReadOnlyCollection<ImapFolderInfo> folders)
    {
        foreach (var folder in folders.Take(25))
        {
            Console.WriteLine($"  {folder.FullName}  role={folder.Role}  selectable={folder.Selectable.ToString().ToLowerInvariant()}  messages={FormatOptional(folder.MessageCount)}  recent={FormatOptional(folder.RecentCount)}  uidvalidity={FormatOptional(folder.UidValidity)}  attrs={folder.Attributes}");
        }

        if (folders.Count > 25)
            Console.WriteLine($"  ... {folders.Count - 25} more");
    }

    private static string FormatOptional(object value)
    {
        return value switch
        {
            null => "-",
            string text when string.IsNullOrWhiteSpace(text) => "-",
            _ => value.ToString()
        };
    }

    private static string FormatSyncState(SyncStateStatus syncState)
    {
        if (syncState is null)
            return "none";

        var success = string.IsNullOrWhiteSpace(syncState.LastSuccessAt) ? "never" : syncState.LastSuccessAt;
        if (string.IsNullOrWhiteSpace(syncState.LastErrorAt))
            return $"{syncState.AccountName}/{syncState.FolderPath} last_success={success}";

        return $"{syncState.AccountName}/{syncState.FolderPath} last_success={success} last_error_at={syncState.LastErrorAt} last_error={syncState.LastError}";
    }

    private static string FormatInitializationKind(DatabaseInitializationKind kind)
    {
        return kind switch
        {
            DatabaseInitializationKind.Opened => "present",
            DatabaseInitializationKind.Created => "created",
            DatabaseInitializationKind.Migrated => "migrated",
            _ => kind.ToString()
        };
    }

    private static void PrintMetadataSyncResult(MetadataAccountSyncResult result)
    {
        Console.WriteLine($"Capabilities: {result.Capabilities}");

        foreach (var folder in result.Folders)
        {
            if (folder.Succeeded)
                Console.WriteLine($"{folder.FolderPath}: requested_since_days={folder.RequestedSinceDays} effective_since_days={folder.EffectiveSinceDays} auto_expanded_for_gap={folder.AutoExpandedForGap.ToString().ToLowerInvariant()} matched={folder.MatchedCount} selected={folder.SelectedCount} fetched={folder.FetchedCount} persisted={folder.PersistedCount} missing={folder.MissingCount} highest_uid={FormatOptional(folder.HighestUid)}");
            else
                Console.WriteLine($"{folder.FolderPath}: requested_since_days={folder.RequestedSinceDays} effective_since_days={folder.EffectiveSinceDays} auto_expanded_for_gap={folder.AutoExpandedForGap.ToString().ToLowerInvariant()} error={folder.Error}");
        }

        var succeeded = result.Folders.Count(folder => folder.Succeeded);
        var persisted = result.Folders.Sum(folder => folder.PersistedCount);
        var failed = result.Folders.Count - succeeded;

        Console.WriteLine($"Metadata sync summary for '{result.AccountId}': folders_ok={succeeded} folders_failed={failed} persisted={persisted}");
    }

    private static void PrintBodySyncResult(BodyAccountSyncResult result)
    {
        if (result.Folders.Count == 0)
        {
            Console.WriteLine($"Body sync summary for '{result.AccountId}': no pending message bodies.");
            return;
        }

        foreach (var folder in result.Folders)
        {
            if (folder.Succeeded)
                Console.WriteLine($"{folder.FolderPath}: selected={folder.SelectedCount} fetched={folder.FetchedCount} persisted={folder.PersistedCount} missing={folder.MissingCount}");
            else
                Console.WriteLine($"{folder.FolderPath}: selected={folder.SelectedCount} fetched={folder.FetchedCount} persisted={folder.PersistedCount} missing={folder.MissingCount} error={folder.Error}");
        }

        var succeeded = result.Folders.Count(folder => folder.Succeeded);
        var persisted = result.Folders.Sum(folder => folder.PersistedCount);
        var failed = result.Folders.Count - succeeded;

        Console.WriteLine($"Body sync summary for '{result.AccountId}': folders_ok={succeeded} folders_failed={failed} persisted={persisted}");
    }

    private static void PrintSearchResult(EmailSearchResult result)
    {
        var from = string.IsNullOrWhiteSpace(result.FromEmail)
            ? "(unknown sender)"
            : string.IsNullOrWhiteSpace(result.FromName)
                ? result.FromEmail
                : $"{result.FromName} <{result.FromEmail}>";
        var subject = string.IsNullOrWhiteSpace(result.Subject) ? "(no subject)" : result.Subject;

        Console.WriteLine();
        Console.WriteLine($"message_id={result.MessageId} account={result.AccountName} score={result.Score:0.###}");
        Console.WriteLine($"date={FormatOptional(result.Date)} folders={FormatOptional(result.Folders)} attachments={result.HasAttachments.ToString().ToLowerInvariant()}");
        Console.WriteLine($"from={from}");
        Console.WriteLine($"subject={subject}");

        if (!string.IsNullOrWhiteSpace(result.Snippet))
            Console.WriteLine($"snippet={result.Snippet}");
    }

    private static void PrintMessageSearchReadiness(MessageSearchReadiness readiness)
    {
        Console.WriteLine(
            $"Message search readiness: {(readiness.SearchReady ? "ready" : "not_synced")} "
            + $"metadata={FormatBool(readiness.MetadataComplete)} bodies={FormatBool(readiness.BodiesComplete)} index={FormatBool(readiness.MessageSearchIndexComplete)}");
        Console.WriteLine(
            $"Message search scope: accounts={readiness.ScopeAccountCount} folders={readiness.ScopeFolderCount} "
            + $"metadata_folders={readiness.MetadataCompleteFolderCount}/{readiness.ScopeFolderCount} "
            + $"metadata_messages={readiness.MetadataMessages} indexed_bodies={readiness.IndexedMessageBodies} "
            + $"search_docs={readiness.MessageSearchDocs} fts_rows={readiness.FtsRows} pending_bodies={readiness.PendingMessageBodies}");

        if (!string.IsNullOrWhiteSpace(readiness.CoverageNote))
            Console.WriteLine($"Message search coverage: {readiness.CoverageNote}");

        if (readiness.ActiveSyncRun is not null)
            Console.WriteLine($"Active sync run: {FormatSyncRun(readiness.ActiveSyncRun)}");
    }

    private static async Task<SyncRunStartResult> WaitForSyncRunLeaseAsync(
        EmailDatabase database,
        SyncRunStartResult syncRun,
        CancellationToken cancellationToken)
    {
        if (syncRun.Acquired)
            return syncRun;

        Console.WriteLine($"Sync queued: {syncRun.Id}");
        if (syncRun.ActiveRun is not null)
            Console.WriteLine($"Waiting behind: {FormatSyncRun(syncRun.ActiveRun)}");

        var lastReportAt = DateTimeOffset.UtcNow;

        while (true)
        {
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken);
            var claim = database.TryClaimQueuedSyncRun(syncRun.Id, syncRun.OwnerId);

            if (claim.Acquired)
            {
                Console.WriteLine($"Sync run started: {claim.Id}");
                return claim;
            }

            if (!claim.Status.Equals("queued", StringComparison.OrdinalIgnoreCase))
                throw new CliException($"Sync run {claim.Id} is no longer queued; status={claim.Status}.", 1);

            var now = DateTimeOffset.UtcNow;
            if ((now - lastReportAt) >= TimeSpan.FromSeconds(30))
            {
                if (claim.ActiveRun is not null)
                    Console.WriteLine($"Still queued behind: {FormatSyncRun(claim.ActiveRun)}");
                else
                    Console.WriteLine($"Still queued: {claim.Id}");

                lastReportAt = now;
            }
        }
    }

    private static async Task<T> RunWithSyncLeaseHeartbeatAsync<T>(
        EmailDatabase database,
        SyncRunStartResult syncRun,
        CancellationToken cancellationToken,
        Func<Task<T>> action,
        Action<SyncRunSnapshot> progressReport = null)
    {
        using var heartbeatCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var heartbeatTask = HeartbeatSyncLeaseAsync(database, syncRun, progressReport, heartbeatCancellation.Token);

        try
        {
            return await action();
        }
        finally
        {
            await StopHeartbeatAsync(heartbeatCancellation, heartbeatTask);
        }
    }

    private static async Task HeartbeatSyncLeaseAsync(
        EmailDatabase database,
        SyncRunStartResult syncRun,
        Action<SyncRunSnapshot> progressReport,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            await Task.Delay(TimeSpan.FromSeconds(30), cancellationToken);
            if (!database.HeartbeatSyncLease(syncRun.Id, syncRun.OwnerId))
                throw new OperationCanceledException("Sync lease was lost.");

            var activeRun = database.ReadActiveSyncRun();
            if (activeRun is not null && activeRun.Id == syncRun.Id)
                progressReport?.Invoke(activeRun);
        }
    }

    private static void ThrowIfSyncLeaseLost(EmailDatabase database, SyncRunStartResult syncRun)
    {
        if (!database.OwnsActiveSyncLease(syncRun.Id, syncRun.OwnerId))
            throw new OperationCanceledException("Sync lease was lost.");
    }

    private static void UpdateSyncRunProgressOrThrow(
        EmailDatabase database,
        SyncRunStartResult syncRun,
        int done,
        int total)
    {
        if (!database.UpdateSyncRunProgress(syncRun.Id, syncRun.OwnerId, done, total))
            throw new OperationCanceledException("Sync lease was lost.");
    }

    private static async Task StopHeartbeatAsync(CancellationTokenSource cancellation, Task heartbeatTask)
    {
        cancellation.Cancel();

        try
        {
            await heartbeatTask;
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static string FormatSyncRun(SyncRunSnapshot run)
    {
        var eta = run.EstimatedRemainingSeconds is int seconds ? seconds.ToString() : "unknown";
        var folder = string.IsNullOrWhiteSpace(run.FolderFilter) ? "all-folders" : run.FolderFilter;
        var requested = run.RequestedSinceDays?.ToString() ?? "unknown";
        var effective = run.EffectiveSinceDays?.ToString() ?? "unknown";
        return $"{run.Id} account={run.AccountName} folder={folder} status={run.Status} phase={run.Phase} requested_since_days={requested} effective_since_days={effective} auto_expanded_for_gap={run.AutoExpandedForGap.ToString().ToLowerInvariant()} done={run.Done}/{run.Total} percent={run.Percent} elapsed={run.ElapsedSeconds}s eta={eta}s confidence={run.EstimateConfidence}";
    }

    private static void PrintSyncProgress(SyncRunSnapshot run)
    {
        if (run is not null)
            Console.WriteLine($"Sync progress: {FormatSyncRun(run)}");
    }

    private static string FormatBool(bool value) =>
        value.ToString().ToLowerInvariant();

    private static string FormatMaxPerFolder(int maxPerFolder) =>
        maxPerFolder == 0 ? "unbounded" : maxPerFolder.ToString();

    private static IReadOnlyList<string> SplitOption(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return [];

        return value
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .ToList();
    }

    private static bool? ParseNullableBool(string value, string optionName)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        if (bool.TryParse(value, out var parsed))
            return parsed;

        if (value.Equals("1", StringComparison.OrdinalIgnoreCase)
            || value.Equals("yes", StringComparison.OrdinalIgnoreCase))
            return true;

        if (value.Equals("0", StringComparison.OrdinalIgnoreCase)
            || value.Equals("no", StringComparison.OrdinalIgnoreCase))
            return false;

        throw new CliException($"{optionName} must be true or false.", 2);
    }

    private static void PrintHelp()
    {
        Console.WriteLine("""
        lcemcp first-stage CLI

        Commands:
          setup-yahoo       Configure a Yahoo IMAP account and store its password in Windows Credential Manager.
          status            Initialize local storage if needed and print config/database status.
          accounts          List configured accounts and whether their credential is present.
          discover-folders  Connect to IMAP, discover folders, and persist account/folder metadata locally.
          folders           List persisted folders from local SQLite storage.
          set-folder-sync   Enable or disable syncing for one persisted folder.
          sync              Sync bounded message envelope metadata into local SQLite storage.
          sync-bodies       Download body text for already-synced message metadata and index it locally.
          search            Search local indexed message metadata and body text.
          serve             Run the MCP stdio server. Writes protocol messages only to stdout.
          credential-test   Check whether an account credential can be found.
          credential-update Update an existing account credential in Windows Credential Manager.
          credential-delete Delete an account credential from Windows Credential Manager.
          imap-test         Connect to IMAP, list folders, search/fetch message summaries, optionally fetch one body.

        Examples:
          dotnet run --project src/LceMcp -- setup-yahoo --email you@yahoo.com --name Yahoo
          dotnet run --project src/LceMcp -- status
          dotnet run --project src/LceMcp -- accounts
          dotnet run --project src/LceMcp -- discover-folders --account yahoo
          dotnet run --project src/LceMcp -- folders --account yahoo
          dotnet run --project src/LceMcp -- set-folder-sync --account yahoo --folder Inbox --enabled true
          dotnet run --project src/LceMcp -- sync --account yahoo --folder Inbox --max-per-folder 50
          dotnet run --project src/LceMcp -- sync-bodies --account yahoo --folder Inbox --max-per-folder 10
          dotnet run --project src/LceMcp -- search --query "refund processed" --account yahoo
          dotnet run --project src/LceMcp -- serve
          dotnet run --project src/LceMcp -- credential-test --account yahoo
          dotnet run --project src/LceMcp -- credential-update --id yahoo
          dotnet run --project src/LceMcp -- imap-test --account yahoo --query "refund processed" --limit 5
          dotnet run --project src/LceMcp -- imap-test --account yahoo --limit 3 --fetch-first-body

        setup-yahoo options:
          --email <email>          Required. Full Yahoo email address.
          --name <name>            Display name. Default: Yahoo.
          --id <id>                Stable local account id. Default: yahoo, or a unique email-derived id.
          --username <username>    IMAP username. Default: email.
          --history-days <days>    Stored default requested sync window. Default: 90.
          --password-stdin         Read password/app password from stdin instead of prompting.
          --skip-password          Write config without storing a credential.

        credential options:
          --account <id-or-email>  Optional when only one account exists.
          --id <id-or-email>       Alias for --account.
          --password-stdin         For credential-update, read the new password/app password from stdin.

        discover-folders/folders options:
          --account <id-or-email>  Required when more than one account exists; optional for folders listing.

        set-folder-sync options:
          --account <id-or-email>  Required. Account owning the folder.
          --folder <path-name-id>  Required. Folder path, display name, or local folder id.
          --enabled <bool>         Required. true to include in default sync, false to skip.

        sync options:
          --account <id-or-email>  Optional. Default: all enabled accounts.
          --folder <path-or-name>  Optional. Default: all cached selectable sync-enabled folders.
          --since-days <days>      Default: account history_days. Use 0 for no date bound; sync may auto-expand to close uncapped-sync gaps.
          --max-per-folder <n>     Default: 200 newest messages per folder. Use 0 for no cap.
          --batch-size <n>         Default: 50. Commits each fetched batch.

        sync-bodies options:
          --account <id-or-email>  Optional. Default: all enabled accounts.
          --folder <path-or-name>  Optional. Default: all cached selectable sync-enabled folders.
          --max-per-folder <n>     Default: 50 pending bodies per folder. Use 0 for no cap.
          --batch-size <n>         Default: 10. Fetches bodies in bounded loops.

        search options:
          --query <text>           Required. Local FTS query text; quoted phrases and OR are supported.
          --account <id-or-email>  Optional. Comma-separated values are accepted.
          --from <email>           Optional sender email filter.
          --to <email>             Optional exact To recipient email filter.
          --date-from <date>       Optional inclusive lower date bound, YYYY-MM-DD or ISO timestamp.
          --date-to <date>         Optional inclusive upper date bound, YYYY-MM-DD or ISO timestamp.
          --folder-role <role>     Optional. Comma-separated roles, e.g. inbox,sent,archive.
          --has-attachment <bool>  Optional true/false metadata filter.
          --limit <n>              Default: 20. Maximum 100.
          --cursor <cursor>        Opaque cursor printed by a prior search page.
          --snippet-chars <n>      Default: 1024. Range 160 to 4096.
          --allow-partial          Debug opt-in. Search even when the requested corpus is not fully indexed.

        imap-test options:
          --account <id-or-email>  Required when more than one account exists.
          --folder <path>          Default: INBOX.
          --query <text>           Server-side search across from/subject/body.
          --since-days <days>      Date bound for the probe. Default: 30. Use 0 for no date bound.
          --limit <n>              Max summaries to fetch. Default: 5.
          --fetch-first-body       Fetch and print a clipped text body for the newest result.
          --body-chars <n>         Body clip length. Default: 1200.
        """);
    }
}
