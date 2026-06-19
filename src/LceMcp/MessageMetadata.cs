namespace LceMcp;

internal sealed record MessageMetadata(
    string ProviderUid,
    string ProviderMessageKey,
    string ProviderThreadKey,
    string MessageIdHeader,
    string InReplyTo,
    string ReferencesHeader,
    string ThreadKey,
    string Subject,
    string NormalizedSubject,
    string FromName,
    string FromEmail,
    string DateSent,
    string DateReceived,
    bool HasAttachments,
    long? SizeBytes,
    string RawHeaders,
    string Flags,
    string Labels);

internal sealed record MetadataFolderSyncResult(
    string FolderPath,
    int MatchedCount,
    int SelectedCount,
    int FetchedCount,
    int MissingCount,
    int PersistedCount,
    uint? HighestUid,
    string Error)
{
    public bool Succeeded => string.IsNullOrWhiteSpace(Error);
}

internal sealed record MetadataAccountSyncResult(
    string AccountId,
    string Capabilities,
    IReadOnlyList<MetadataFolderSyncResult> Folders);
