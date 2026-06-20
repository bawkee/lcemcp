namespace LceMcp;

internal sealed record StoredEmailMessage(
    int MessageId,
    string AccountName,
    string AccountEmailAddress,
    string DateSent,
    string DateReceived,
    string FromName,
    string FromEmail,
    string Subject,
    bool HasAttachments,
    string BodyText,
    IReadOnlyList<string> Folders,
    IReadOnlyList<MessageRecipient> Recipients);
