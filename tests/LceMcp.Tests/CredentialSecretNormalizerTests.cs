namespace LceMcp.Tests;

public sealed class CredentialSecretNormalizerTests
{
    [Fact]
    public void GmailAppPasswordWhitespaceIsPresentationOnly()
    {
        var normalized = CredentialSecretNormalizer.Normalize("GMAIL", "abcd efgh\tijkl\r\nmnop");

        Assert.Equal("abcdefghijklmnop", normalized);
    }

    [Fact]
    public void OtherProviderSecretsAreNotChanged()
    {
        var normalized = CredentialSecretNormalizer.Normalize("custom", "secret with spaces");

        Assert.Equal("secret with spaces", normalized);
    }
}
