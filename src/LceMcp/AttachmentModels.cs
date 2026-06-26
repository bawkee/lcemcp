namespace LceMcp;

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
    IReadOnlyList<AttachmentContent> Children);

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
    bool OcrTextAvailable);

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
