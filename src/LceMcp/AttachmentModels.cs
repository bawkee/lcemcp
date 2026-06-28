namespace LceMcp;

// A processed node in the attachment tree. Email MIME parts are roots; archive
// entries are children, and each node carries the latest download/extraction result.
internal sealed record AttachmentContent(
    string SourceKind,
    string PartId,
    string Filename,
    string DisplayPath,
    string ArchiveEntryPath,
    string MimeType,
    string SniffedMimeType,
    long? SizeBytes,
    long? CompressedSizeBytes,
    long? UncompressedSizeBytes,
    string ContentHash,
    string StorageKey,
    bool IsContainer,
    int NestingDepth,
    string DownloadStatus,
    string DownloadError,
    string ExtractionStatus,
    string ExtractionError,
    string ExtractedText,
    string OcrText,
    string Extractor,
    IReadOnlyList<AttachmentContent> Children,
    string ExtractionErrorCode = null,
    string ExtractorVersion = null,
    string ExceptionType = null,
    string ExceptionMessage = null,
    string ExceptionDetails = null,
    string DownloadErrorCode = null);

// The database's latest-state projection for one stable local attachment ID.
// Execution history and deduplicated failure issues live in separate records/tables.
internal sealed record StoredAttachment(
    int AttachmentId,
    int MessageId,
    int? ParentAttachmentId,
    int? RootAttachmentId,
    string SourceKind,
    string PartId,
    string Filename,
    string DisplayPath,
    string ArchiveEntryPath,
    string MimeType,
    string SniffedMimeType,
    long? SizeBytes,
    long? CompressedSizeBytes,
    long? UncompressedSizeBytes,
    string ContentHash,
    string StorageKey,
    bool IsContainer,
    int NestingDepth,
    string DownloadStatus,
    string DownloadError,
    string ExtractionStatus,
    string ExtractionError,
    bool ExtractedTextAvailable,
    bool OcrTextAvailable,
    string ExtractionErrorCode = null,
    string ExtractionNextAttemptAt = null,
    string ExtractionCompletedAt = null,
    string Extractor = null,
    string ExtractorVersion = null);

internal sealed record AttachmentTextContent(
    StoredAttachment Attachment,
    string ExtractedText,
    string OcrText,
    string CombinedText,
    string Extractor,
    string ExtractedAt);

internal sealed record PreparedAttachmentAccess(
    StoredAttachment Attachment,
    string Kind,
    string Path,
    string Error);

internal sealed record StoredAttachmentObject(
    string StorageKey,
    string ContentHash,
    long SizeBytes);

internal sealed record AttachmentSearchMatch(
    StoredAttachment Attachment,
    string Snippet,
    double Score);

internal sealed record AttachmentExtractionFailureQuery(
    IReadOnlyList<int> AttachmentIds,
    IReadOnlyList<string> AccountFilters,
    IReadOnlyList<string> ErrorCodes,
    string Status,
    int Limit);

internal sealed record AttachmentExtractionFailure(
    int FailureId,
    int AttachmentId,
    int MessageId,
    string AccountName,
    string DisplayPath,
    string MimeType,
    string Stage,
    string ErrorCode,
    string ErrorSummary,
    string ExceptionType,
    string Extractor,
    string ExtractorVersion,
    int OccurrenceCount,
    string FirstSeenAt,
    string LastCheckedAt,
    string ResolvedAt,
    string Status);

internal sealed record AttachmentExtractionClaim(
    long AttemptId,
    string LeaseToken,
    string TriggerKind,
    StoredAttachment Attachment);

internal sealed record AttachmentRetryItemResult(
    int AttachmentId,
    string Status,
    string ErrorCode,
    string Error);

internal sealed record AttachmentRetryResult(
    IReadOnlyList<AttachmentRetryItemResult> Items)
{
    public int SelectedCount => Items.Count;
    public int SucceededCount => Items.Count(item => item.Status is "done" or "empty");
    public int FailedCount => Items.Count(item => item.Status == "failed");
    public int SkippedCount => Items.Count - SucceededCount - FailedCount;
}

internal sealed record AttachmentExtractionInput(
    string Filename,
    string MimeType,
    byte[] Content);

internal sealed record AttachmentExtractionOutput(
    string Text,
    string Extractor,
    string ExtractorVersion = AttachmentProcessor.ProcessorVersion);
