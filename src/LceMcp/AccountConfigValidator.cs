namespace LceMcp;

internal static class AccountConfigValidator
{
    private static readonly HashSet<string> SupportedImapSecurity = new(StringComparer.OrdinalIgnoreCase)
    {
        "ssl",
        "ssl/tls",
        "starttls",
        "none"
    };

    public static IReadOnlyList<string> ValidateForImap(AccountConfig account)
    {
        var errors = new List<string>();

        AddRequired(errors, account.Id, "id");
        AddRequired(errors, account.EmailAddress, "email_address");
        AddRequired(errors, account.Username, "username");
        AddRequired(errors, account.ImapHost, "imap_host");
        AddRequired(errors, account.ImapSecurity, "imap_security");
        AddRequired(errors, account.CredentialRef, "credential_ref");

        if (account.ImapPort is < 1 or > 65535)
            errors.Add("imap_port must be between 1 and 65535");

        if (!string.IsNullOrWhiteSpace(account.ImapSecurity)
            && !SupportedImapSecurity.Contains(account.ImapSecurity))
            errors.Add("imap_security must be one of ssl, ssl/tls, starttls, or none");

        if (account.HistoryDays < 1)
            errors.Add("history_days must be at least 1");

        return errors;
    }

    public static void ThrowIfInvalidForImap(AccountConfig account)
    {
        var errors = ValidateForImap(account);
        if (errors.Count == 0)
            return;

        var accountName = string.IsNullOrWhiteSpace(account.Id) ? "(missing id)" : account.Id;
        throw new CliException($"Account '{accountName}' is not ready for IMAP: {string.Join("; ", errors)}.", 2);
    }

    private static void AddRequired(List<string> errors, string value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
            errors.Add($"{name} is required");
    }
}
