namespace LceMcp;

internal sealed class AccountConfig
{
    public string Id { get; init; } = "";
    public string DisplayName { get; init; } = "";
    public string EmailAddress { get; init; } = "";
    public string Provider { get; init; } = "";
    public string Username { get; init; } = "";
    public string ImapHost { get; init; } = "";
    public int ImapPort { get; init; }
    public string ImapSecurity { get; init; } = "ssl";
    public int HistoryDays { get; init; } = 90;
    public string AttachmentPolicy { get; init; } = "metadata_only";
    public string CredentialRef { get; init; } = "";
    public bool Enabled { get; init; } = true;
}
