namespace LceMcp.Tests;

public sealed class WindowsCredentialStoreTests
{
    [Fact]
    public void BuildImapTargetUsesStableAccountScopedPrefix()
    {
        var target = WindowsCredentialStore.BuildImapTarget("yahoo");

        Assert.Equal("lcemcp/imap/yahoo", target);
    }
}
