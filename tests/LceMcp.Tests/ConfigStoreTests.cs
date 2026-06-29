namespace LceMcp.Tests;

public sealed class ConfigStoreTests
{
    [Fact]
    public void SaveAndLoadRoundTripsAccountsWithoutSecrets()
    {
        using var temp = TempWorkspace.Create();
        var store = new ConfigStore(temp.Paths);
        var config = new AppConfig { Version = 1 };
        config.Ocr.Enabled = true;
        config.Ocr.AutoDownloadLanguagePacks = false;
        config.Ocr.FallbackScript = "Cyrillic";
        config.Ocr.Languages.AddRange(["eng", "srp", "srp_latn"]);
        config.Accounts.Add(new AccountConfig
        {
            Id = "work",
            DisplayName = "Work \"Archive\"",
            EmailAddress = "work@example.com",
            Provider = "custom",
            Username = "work.user",
            ImapHost = "imap.example.com",
            ImapPort = 1993,
            ImapSecurity = "starttls",
            HistoryDays = 45,
            AttachmentPolicy = "metadata_only",
            CredentialRef = "lcemcp/imap/work",
            Enabled = false
        });
        config.Accounts.Add(TestData.Account(id: "yahoo", email: "person@yahoo.com"));

        store.Save(config);

        var loaded = store.Load();
        var savedText = File.ReadAllText(store.ConfigPath);

        Assert.Equal(1, loaded.Version);
        Assert.True(loaded.Ocr.Enabled);
        Assert.False(loaded.Ocr.AutoDownloadLanguagePacks);
        Assert.Equal("Cyrillic", loaded.Ocr.FallbackScript);
        Assert.Equal(["eng", "srp", "srp_latn"], loaded.Ocr.Languages);
        Assert.Equal(["work", "yahoo"], loaded.Accounts.Select(account => account.Id).ToArray());
        Assert.DoesNotContain("super-secret", savedText, StringComparison.OrdinalIgnoreCase);

        var work = loaded.FindAccount("WORK");
        Assert.NotNull(work);
        Assert.Equal("Work \"Archive\"", work.DisplayName);
        Assert.Equal("work@example.com", work.EmailAddress);
        Assert.Equal("custom", work.Provider);
        Assert.Equal("work.user", work.Username);
        Assert.Equal("imap.example.com", work.ImapHost);
        Assert.Equal(1993, work.ImapPort);
        Assert.Equal("starttls", work.ImapSecurity);
        Assert.Equal(45, work.HistoryDays);
        Assert.Equal("metadata_only", work.AttachmentPolicy);
        Assert.Equal("lcemcp/imap/work", work.CredentialRef);
        Assert.False(work.Enabled);
    }
}
