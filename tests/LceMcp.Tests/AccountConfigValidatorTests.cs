namespace LceMcp.Tests;

public sealed class AccountConfigValidatorTests
{
    [Fact]
    public void ValidateForImapAcceptsCompleteAccount()
    {
        var errors = AccountConfigValidator.ValidateForImap(TestData.Account());

        Assert.Empty(errors);
    }

    [Fact]
    public void ValidateForImapReportsMissingAndInvalidSettings()
    {
        var account = new AccountConfig
        {
            Id = "",
            EmailAddress = "",
            Username = "",
            ImapHost = "",
            ImapPort = 70000,
            ImapSecurity = "tls-ish",
            HistoryDays = 0,
            CredentialRef = ""
        };

        var errors = AccountConfigValidator.ValidateForImap(account);

        Assert.Contains("id is required", errors);
        Assert.Contains("email_address is required", errors);
        Assert.Contains("username is required", errors);
        Assert.Contains("imap_host is required", errors);
        Assert.Contains("credential_ref is required", errors);
        Assert.Contains("imap_port must be between 1 and 65535", errors);
        Assert.Contains("imap_security must be one of ssl, ssl/tls, starttls, or none", errors);
        Assert.Contains("history_days must be at least 1", errors);
    }
}
