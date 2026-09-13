namespace LceMcp;

internal static class CredentialSecretNormalizer
{
    public static string Normalize(string provider, string secret)
    {
        if (!provider.Equals("gmail", StringComparison.OrdinalIgnoreCase))
            return secret;

        // Google displays app passwords in grouped blocks. Whitespace is presentation,
        // not part of the credential accepted by Gmail's IMAP endpoint.
        return string.Concat(secret.Where(character => !char.IsWhiteSpace(character)));
    }
}
