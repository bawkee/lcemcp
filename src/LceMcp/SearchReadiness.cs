namespace LceMcp;

internal sealed record MessageSearchReadinessRequest(
    IReadOnlyList<string> AccountFilters,
    string FromEmail,
    IReadOnlyList<string> FolderRoles,
    bool? HasAttachment,
    string ToEmail = null,
    string DateFrom = null,
    string DateTo = null,
    bool IncludeAttachments = false,
    IReadOnlyList<string> MimeTypes = null,
    string FilenameContains = null);

internal sealed record MessageSearchReadiness(
    bool SearchReady,
    bool MetadataComplete,
    bool BodiesComplete,
    bool MessageSearchIndexComplete,
    bool AttachmentSearchIndexComplete,
    int ScopeAccountCount,
    int ScopeFolderCount,
    int MetadataCompleteFolderCount,
    int MetadataMessages,
    int IndexedMessageBodies,
    int MessageSearchDocs,
    int FtsRows,
    int PendingMessageBodies,
    int Attachments,
    int AttachmentSearchDocs,
    int AttachmentFtsRows,
    int PendingAttachments,
    int PendingAttachmentMessages,
    SyncRunSnapshot ActiveSyncRun,
    string CoverageNote = null,
    SearchFreshness Freshness = null,
    int AttachmentTexts = 0,
    int OpenAttachmentExtractionFailures = 0,
    IReadOnlyDictionary<string, int> AttachmentExtractionFailuresByCode = null);

internal sealed record SearchFreshness(
    string ResponseGeneratedAt,
    string SearchScopeAsOf,
    string LastSyncPerformedAt,
    string OldestScopedSyncAt,
    string NewestScopedSyncAt,
    int? CacheAgeSeconds,
    string RequestedDateFrom,
    string RequestedDateTo,
    string RequestedUpperBound,
    bool RequestedRangeExtendsBeyondCache);

internal sealed record SyncRunSnapshot(
    string Id,
    string ScopeKey,
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
    string LastError,
    int? RequestedSinceDays = null,
    int? EffectiveSinceDays = null,
    bool AutoExpandedForGap = false);

internal sealed record SyncRunStartResult(
    string Id,
    string ScopeKey,
    string OwnerId,
    string Status,
    bool Acquired,
    SyncRunSnapshot ActiveRun);
