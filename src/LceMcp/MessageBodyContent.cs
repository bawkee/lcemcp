namespace LceMcp;

internal sealed record MessageBodyContent(
    int MessageId,
    string PlainText,
    string HtmlText,
    string NormalizedText,
    IReadOnlyList<MessageRecipient> Recipients);

internal sealed record MessageRecipient(
    string Type,
    string Name,
    string Email);

internal sealed record BodySyncTarget(
    int MessageId,
    int FolderId,
    string FolderPath,
    string ProviderUid,
    string Subject,
    bool HasAttachments,
    long? SizeBytes);

internal sealed record BodyFolderSyncResult(
    string FolderPath,
    int SelectedCount,
    int FetchedCount,
    int PersistedCount,
    int MissingCount,
    string Error)
{
    public bool Succeeded => string.IsNullOrWhiteSpace(Error);
}

internal sealed record BodyAccountSyncResult(
    string AccountId,
    IReadOnlyList<BodyFolderSyncResult> Folders);

internal sealed record EmailSearchRequest(
    string Query,
    IReadOnlyList<string> AccountFilters,
    string FromEmail,
    IReadOnlyList<string> FolderRoles,
    bool? HasAttachment,
    int Limit,
    int SnippetChars,
    bool AllowPartial = false,
    string ToEmail = null,
    string DateFrom = null,
    string DateTo = null,
    string Cursor = null)
{
    public bool HasTextQuery => !string.IsNullOrWhiteSpace(Query);

    public bool HasMetadataFilters =>
        HasValues(AccountFilters)
        || !string.IsNullOrWhiteSpace(FromEmail)
        || HasValues(FolderRoles)
        || HasAttachment.HasValue
        || !string.IsNullOrWhiteSpace(ToEmail)
        || !string.IsNullOrWhiteSpace(DateFrom)
        || !string.IsNullOrWhiteSpace(DateTo);

    public bool IsBounded => HasTextQuery || HasMetadataFilters;

    private static bool HasValues(IReadOnlyList<string> values) =>
        values is not null && values.Any(value => !string.IsNullOrWhiteSpace(value));
}

internal sealed record EmailSearchResult(
    int MessageId,
    string AccountName,
    string Folders,
    string Date,
    string FromName,
    string FromEmail,
    string Subject,
    bool HasAttachments,
    string Snippet,
    double Score,
    string Cursor = null);
