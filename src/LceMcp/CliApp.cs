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

        return command switch
        {
            "setup-yahoo" => await SetupYahooAsync(configStore, credentialStore, options, cancellationToken),
            "accounts" => ListAccounts(configStore, credentialStore),
            "credential-test" => CredentialTest(configStore, credentialStore, options),
            "credential-delete" => CredentialDelete(configStore, credentialStore, options),
            "imap-test" => await ImapTestAsync(configStore, credentialStore, options, cancellationToken),
            "help" => PrintHelpAndReturn(),
            _ => throw new CliException($"Unknown command '{args[0]}'. Run 'lcemcp help'.", 2)
        };
    }

    private static async Task<int> SetupYahooAsync(
        ConfigStore configStore,
        WindowsCredentialStore credentialStore,
        CommandOptions options,
        CancellationToken cancellationToken)
    {
        var email = options.GetRequired("--email").Trim();
        var displayName = options.Get("--name") ?? "Yahoo";
        var username = options.Get("--username") ?? email;
        var historyDays = options.GetInt("--history-days", 30);

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
        cancellationToken.ThrowIfCancellationRequested();

        Console.WriteLine($"Saved account '{account.Id}' to {configStore.ConfigPath}");
        Console.WriteLine("Next test:");
        Console.WriteLine($"  dotnet run --project src/LceMcp -- imap-test --account {account.Id} --limit 5");
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
            Console.WriteLine($"{account.Id}  {account.EmailAddress}  provider={account.Provider}  enabled={account.Enabled}  credential={credentialStatus}");
            Console.WriteLine($"  imap={account.ImapHost}:{account.ImapPort}/{account.ImapSecurity}  user={account.Username}  history_days={account.HistoryDays}");
        }

        return 0;
    }

    private static int CredentialTest(ConfigStore configStore, WindowsCredentialStore credentialStore, CommandOptions options)
    {
        var account = ResolveAccount(configStore.Load(), options.Get("--account"));

        if (string.IsNullOrWhiteSpace(account.CredentialRef))
            throw new CliException($"Account '{account.Id}' has no credential_ref in config.", 2);

        var exists = credentialStore.Exists(account.CredentialRef);
        Console.WriteLine(exists
            ? $"Credential exists: {account.CredentialRef}"
            : $"Credential not found: {account.CredentialRef}");

        return exists ? 0 : 3;
    }

    private static int CredentialDelete(ConfigStore configStore, WindowsCredentialStore credentialStore, CommandOptions options)
    {
        var account = ResolveAccount(configStore.Load(), options.Get("--account"));

        if (string.IsNullOrWhiteSpace(account.CredentialRef))
            throw new CliException($"Account '{account.Id}' has no credential_ref in config.", 2);

        var deleted = credentialStore.Delete(account.CredentialRef);
        Console.WriteLine(deleted
            ? $"Deleted credential: {account.CredentialRef}"
            : $"Credential was already absent: {account.CredentialRef}");

        return 0;
    }

    private static async Task<int> ImapTestAsync(
        ConfigStore configStore,
        WindowsCredentialStore credentialStore,
        CommandOptions options,
        CancellationToken cancellationToken)
    {
        var account = ResolveAccount(configStore.Load(), options.Get("--account"));

        if (!account.Enabled)
            throw new CliException($"Account '{account.Id}' is disabled.", 2);

        if (string.IsNullOrWhiteSpace(account.CredentialRef))
            throw new CliException($"Account '{account.Id}' has no credential_ref in config.", 2);

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

    private static void PrintHelp()
    {
        Console.WriteLine("""
        lcemcp first-stage CLI

        Commands:
          setup-yahoo       Configure a Yahoo IMAP account and store its password in Windows Credential Manager.
          accounts          List configured accounts and whether their credential is present.
          credential-test   Check whether an account credential can be found.
          credential-delete Delete an account credential from Windows Credential Manager.
          imap-test         Connect to IMAP, list folders, search/fetch message summaries, optionally fetch one body.

        Examples:
          dotnet run --project src/LceMcp -- setup-yahoo --email you@yahoo.com --name Yahoo
          dotnet run --project src/LceMcp -- accounts
          dotnet run --project src/LceMcp -- credential-test --account yahoo
          dotnet run --project src/LceMcp -- imap-test --account yahoo --query "refund processed" --limit 5
          dotnet run --project src/LceMcp -- imap-test --account yahoo --limit 3 --fetch-first-body

        setup-yahoo options:
          --email <email>          Required. Full Yahoo email address.
          --name <name>            Display name. Default: Yahoo.
          --id <id>                Stable local account id. Default: yahoo, or a unique email-derived id.
          --username <username>    IMAP username. Default: email.
          --history-days <days>    Stored sync preference. Default: 30 for development.
          --password-stdin         Read password/app password from stdin instead of prompting.
          --skip-password          Write config without storing a credential.

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
