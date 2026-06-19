namespace LceMcp;

internal sealed record MessageSearchReadinessRequest(
    IReadOnlyList<string> AccountFilters,
    string FromEmail,
    IReadOnlyList<string> FolderRoles,
    bool? HasAttachment);

internal sealed record MessageSearchReadiness(
    bool SearchReady,
    bool MetadataComplete,
    bool BodiesComplete,
    bool MessageSearchIndexComplete,
    int ScopeAccountCount,
    int ScopeFolderCount,
    int MetadataCompleteFolderCount,
    int MetadataMessages,
    int IndexedMessageBodies,
    int MessageSearchDocs,
    int FtsRows,
    int PendingMessageBodies,
    SyncRunSnapshot ActiveSyncRun);

internal sealed record SyncRunSnapshot(
    string Id,
    string AccountName,
    string FolderFilter,
    string Status,
    string Phase,
    int Done,
    int Total,
    int Percent,
    int ElapsedSeconds,
    int? EstimatedRemainingSeconds,
    string EstimateConfidence,
    string StartedAt,
    string LastProgressAt,
    string CompletedAt,
    string LastError);
