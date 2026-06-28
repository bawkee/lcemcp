namespace LceMcp;

internal sealed record MessageBodyContent(
    int MessageId,
    string PlainText,
    string HtmlText,
    string NormalizedText,
    IReadOnlyList<MessageRecipient> Recipients,
    IReadOnlyList<AttachmentContent> Attachments = null);

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
    string Error,
    int FailedCount = 0)
{
    public bool Succeeded => string.IsNullOrWhiteSpace(Error) && FailedCount == 0;
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
    string Cursor = null,
    IReadOnlyList<string> SearchIn = null,
    IReadOnlyList<string> MimeTypes = null,
    string FilenameContains = null,
    bool IncludeAttachmentMetadata = true,
    int MaxAttachmentHitsPerMessage = 5)
{
    public bool HasTextQuery => !string.IsNullOrWhiteSpace(Query);

    public bool SearchesMessages =>
        !HasValues(SearchIn)
        || SearchIn.Any(value => value.Equals("messages", StringComparison.OrdinalIgnoreCase));

    public bool SearchesAttachments =>
        SearchIn?.Any(value => value.Equals("attachments", StringComparison.OrdinalIgnoreCase)) == true
        || HasValues(MimeTypes)
        || !string.IsNullOrWhiteSpace(FilenameContains);

    public bool HasMetadataFilters =>
        HasValues(AccountFilters)
        || !string.IsNullOrWhiteSpace(FromEmail)
        || HasValues(FolderRoles)
        || HasAttachment.HasValue
        || !string.IsNullOrWhiteSpace(ToEmail)
        || !string.IsNullOrWhiteSpace(DateFrom)
        || !string.IsNullOrWhiteSpace(DateTo)
        || HasValues(MimeTypes)
        || !string.IsNullOrWhiteSpace(FilenameContains);

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
    string Cursor = null,
    IReadOnlyList<AttachmentSearchMatch> MatchingAttachments = null);
