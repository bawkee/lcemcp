namespace LceMcp.Tests;

internal static class TestData
{
    public static AccountConfig Account(
        string id = "yahoo",
        string email = "person@yahoo.com",
        string imapHost = "imap.mail.yahoo.com",
        string imapSecurity = "ssl") =>
        new()
        {
            Id = id,
            DisplayName = "Yahoo",
            EmailAddress = email,
            Provider = "yahoo",
            Username = email,
            ImapHost = imapHost,
            ImapPort = 993,
            ImapSecurity = imapSecurity,
            HistoryDays = 30,
            AttachmentPolicy = "metadata_only",
            CredentialRef = WindowsCredentialStore.BuildImapTarget(id),
            Enabled = true
        };

    public static ImapFolderInfo Folder(
        string path,
        string role = "custom",
        string attributes = "",
        bool selectable = true,
        int? messageCount = null) =>
        new(
            FullName: path,
            Name: path.Split('/').Last(),
            Delimiter: "/",
            Attributes: attributes,
            Role: role,
            Selectable: selectable,
            UidValidity: "123",
            MessageCount: messageCount,
            RecentCount: 0,
            StatusError: null);

    public static MessageMetadata Message(
        string providerUid,
        string providerMessageKey = "emailid:abc",
        string messageIdHeader = "abc@example.com",
        string subject = "Original subject",
        string flags = null) =>
        new(
            ProviderUid: providerUid,
            ProviderMessageKey: providerMessageKey,
            ProviderThreadKey: "threadid:thread-1",
            MessageIdHeader: messageIdHeader,
            InReplyTo: null,
            ReferencesHeader: null,
            ThreadKey: "threadid:thread-1",
            Subject: subject,
            NormalizedSubject: subject?.ToLowerInvariant(),
            FromName: "Sender",
            FromEmail: "sender@example.com",
            DateSent: "2026-06-19T10:00:00.0000000+00:00",
            DateReceived: "2026-06-19T10:01:00.0000000+00:00",
            HasAttachments: false,
            SizeBytes: 1234,
            RawHeaders: null,
            Flags: flags,
            Labels: null);
}
