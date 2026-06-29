using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using UglyToad.PdfPig;

namespace LceMcp;

internal sealed class AttachmentProcessor
{
    internal const long MaxAttachmentBytes = 25L * 1024 * 1024;
    private const int MaxExtractedChars = 1_000_000;
    private const int MaxArchiveDepth = 3;
    private const int MaxArchiveEntries = 200;
    private const long MaxArchiveEntryBytes = 25L * 1024 * 1024;
    private const long MaxArchiveTotalUncompressedBytes = 100L * 1024 * 1024;
    private const double MaxArchiveCompressionRatio = 100d;
    private const int MinUsefulPdfPageCharacters = 24;
    internal const string ProcessorVersion = "2";
    // Terminal means a leaf document such as PDF/DOCX/TXT, as opposed to a ZIP
    // container. The process-wide gate prevents multiple timed-out, non-cancelable
    // library calls from accumulating; a timed-out task retains the gate until it exits.
    private static readonly SemaphoreSlim TerminalExtractionGate = new(1, 1);
    private static readonly TimeSpan DefaultExtractionTimeout = TimeSpan.FromSeconds(30);

    private readonly AttachmentObjectStore _objectStore;
    private readonly Func<AttachmentExtractionInput, AttachmentExtractionOutput> _terminalExtractor;
    private readonly TimeSpan _extractionTimeout;
    private readonly OcrConfig _ocrConfig;
    private readonly IPdfOcrExtractor _pdfOcrExtractor;

    public AttachmentProcessor(
        AttachmentObjectStore objectStore,
        Func<AttachmentExtractionInput, AttachmentExtractionOutput> terminalExtractor = null,
        TimeSpan? extractionTimeout = null,
        OcrConfig ocrConfig = null,
        IPdfOcrExtractor pdfOcrExtractor = null)
    {
        _objectStore = objectStore;
        _terminalExtractor = terminalExtractor;
        _ocrConfig = ocrConfig ?? new();
        _extractionTimeout = extractionTimeout
            ?? (_ocrConfig.Enabled ? TimeSpan.FromMinutes(2) : DefaultExtractionTimeout);
        _pdfOcrExtractor = pdfOcrExtractor
            ?? (_ocrConfig.Enabled ? new PdfOcrExtractor(objectStore.Paths, _ocrConfig) : null);
    }

    public AttachmentContent ProcessEmailAttachment(
        string partId,
        string filename,
        string mimeType,
        long? declaredSizeBytes,
        byte[] content,
        string displayPath)
    {
        return ProcessStoredAttachment(
            SourceKind: "email_part",
            PartId: partId,
            Filename: filename,
            DisplayPath: displayPath,
            ArchiveEntryPath: null,
            MimeType: mimeType,
            DeclaredSizeBytes: declaredSizeBytes,
            CompressedSizeBytes: null,
            UncompressedSizeBytes: content?.LongLength ?? declaredSizeBytes,
            Content: content,
            NestingDepth: 0);
    }

    public AttachmentContent ProcessStoredAttachment(StoredAttachment attachment, byte[] content) =>
        ProcessStoredAttachment(
            SourceKind: attachment.SourceKind,
            PartId: attachment.PartId,
            Filename: attachment.Filename,
            DisplayPath: attachment.DisplayPath,
            ArchiveEntryPath: attachment.ArchiveEntryPath,
            MimeType: attachment.MimeType,
            DeclaredSizeBytes: attachment.SizeBytes,
            CompressedSizeBytes: attachment.CompressedSizeBytes,
            UncompressedSizeBytes: attachment.UncompressedSizeBytes,
            Content: content,
            NestingDepth: attachment.NestingDepth);

    public AttachmentContent RejectEmailAttachment(
        string partId,
        string filename,
        string mimeType,
        long? declaredSizeBytes,
        string displayPath,
        string errorCode,
        string error,
        Exception exception = null)
    {
        var safeFilename = BlankToNull(Path.GetFileName((filename ?? "").Replace('\\', '/'))) ?? "attachment";
        var safeDisplayPath = BlankToNull(displayPath) ?? safeFilename;

        return Node(
            SourceKind: "email_part",
            PartId: partId,
            Filename: safeFilename,
            DisplayPath: safeDisplayPath,
            ArchiveEntryPath: null,
            MimeType: mimeType,
            SniffedMimeType: GuessMimeType(safeFilename),
            SizeBytes: declaredSizeBytes,
            CompressedSizeBytes: null,
            UncompressedSizeBytes: declaredSizeBytes,
            ContentHash: null,
            StorageKey: null,
            IsContainer: IsZip(mimeType ?? "", safeFilename) || IsSevenZip(mimeType ?? "", safeFilename),
            NestingDepth: 0,
            DownloadStatus: errorCode == "attachment_too_large" ? "skipped" : "failed",
            DownloadError: error,
            ExtractionStatus: "failed",
            ExtractionError: error,
            ExtractedText: null,
            Extractor: null,
            Children: [],
            ExtractionErrorCode: errorCode,
            Exception: exception,
            DownloadErrorCode: errorCode);
    }

    public AttachmentContent CreateEmailAttachmentMetadata(
        string partId,
        string filename,
        string mimeType,
        long? declaredSizeBytes,
        string displayPath)
    {
        var safeFilename = BlankToNull(Path.GetFileName((filename ?? "").Replace('\\', '/'))) ?? "attachment";
        var safeDisplayPath = BlankToNull(displayPath) ?? safeFilename;
        var sniffedMimeType = GuessMimeType(safeFilename);
        return Node(
            SourceKind: "email_part",
            PartId: partId,
            Filename: safeFilename,
            DisplayPath: safeDisplayPath,
            ArchiveEntryPath: null,
            MimeType: mimeType,
            SniffedMimeType: sniffedMimeType,
            SizeBytes: declaredSizeBytes,
            CompressedSizeBytes: null,
            UncompressedSizeBytes: declaredSizeBytes,
            ContentHash: null,
            StorageKey: null,
            IsContainer: IsZip(sniffedMimeType, safeFilename) || IsSevenZip(sniffedMimeType, safeFilename),
            NestingDepth: 0,
            DownloadStatus: "pending",
            DownloadError: null,
            ExtractionStatus: "not_ready",
            ExtractionError: null,
            ExtractedText: null,
            Extractor: null,
            Children: []);
    }

    private AttachmentContent ProcessArchiveEntry(
        string parentDisplayPath,
        string archiveEntryPath,
        string displayEntryPath,
        string filename,
        long compressedSizeBytes,
        long uncompressedSizeBytes,
        byte[] content,
        int nestingDepth,
        ArchiveProcessingBudget archiveBudget)
    {
        return ProcessStoredAttachment(
            SourceKind: "archive_entry",
            PartId: null,
            Filename: filename,
            DisplayPath: $"{parentDisplayPath}!/{displayEntryPath}",
            ArchiveEntryPath: archiveEntryPath,
            MimeType: GuessMimeType(filename),
            DeclaredSizeBytes: uncompressedSizeBytes,
            CompressedSizeBytes: compressedSizeBytes,
            UncompressedSizeBytes: uncompressedSizeBytes,
            Content: content,
            NestingDepth: nestingDepth,
            ArchiveBudget: archiveBudget);
    }

    private AttachmentContent ProcessStoredAttachment(
        string SourceKind,
        string PartId,
        string Filename,
        string DisplayPath,
        string ArchiveEntryPath,
        string MimeType,
        long? DeclaredSizeBytes,
        long? CompressedSizeBytes,
        long? UncompressedSizeBytes,
        byte[] Content,
        int NestingDepth,
        ArchiveProcessingBudget ArchiveBudget = null)
    {
        var safeFilename = BlankToNull(Path.GetFileName((Filename ?? "").Replace('\\', '/'))) ?? "attachment";
        var displayPath = BlankToNull(DisplayPath) ?? safeFilename;
        var sniffedMimeType = SniffMimeType(safeFilename, MimeType, Content);
        var isContainer = IsZip(sniffedMimeType, safeFilename) || IsSevenZip(sniffedMimeType, safeFilename);

        if (Content is null)
        {
            return Node(
                SourceKind,
                PartId,
                safeFilename,
                displayPath,
                ArchiveEntryPath,
                MimeType,
                sniffedMimeType,
                DeclaredSizeBytes,
                CompressedSizeBytes,
                UncompressedSizeBytes,
                ContentHash: null,
                StorageKey: null,
                IsContainer: isContainer,
                NestingDepth,
                DownloadStatus: "failed",
                DownloadError: "Attachment content was not available.",
                ExtractionStatus: "failed",
                ExtractionError: "Attachment content was not available.",
                ExtractedText: null,
                Extractor: null,
                Children: [],
                ExtractionErrorCode: "temporary_io_failure",
                DownloadErrorCode: "temporary_io_failure");
        }

        if (Content.LongLength > MaxAttachmentBytes)
        {
            return Node(
                SourceKind,
                PartId,
                safeFilename,
                displayPath,
                ArchiveEntryPath,
                MimeType,
                sniffedMimeType,
                Content.LongLength,
                CompressedSizeBytes,
                UncompressedSizeBytes,
                ContentHash: null,
                StorageKey: null,
                IsContainer: isContainer,
                NestingDepth,
                DownloadStatus: "skipped",
                DownloadError: $"Attachment exceeds {MaxAttachmentBytes} byte storage limit.",
                ExtractionStatus: "failed",
                ExtractionError: $"Attachment exceeds {MaxAttachmentBytes} byte storage limit.",
                ExtractedText: null,
                Extractor: null,
                Children: [],
                ExtractionErrorCode: "attachment_too_large",
                DownloadErrorCode: "attachment_too_large");
        }

        var stored = _objectStore.Store(Content);

        if (IsSevenZip(sniffedMimeType, safeFilename))
        {
            return Node(
                SourceKind,
                PartId,
                safeFilename,
                displayPath,
                ArchiveEntryPath,
                MimeType,
                sniffedMimeType,
                stored.SizeBytes,
                CompressedSizeBytes,
                UncompressedSizeBytes,
                stored.ContentHash,
                stored.StorageKey,
                IsContainer: true,
                NestingDepth,
                DownloadStatus: "stored",
                DownloadError: null,
                ExtractionStatus: "failed",
                ExtractionError: "7z archive expansion is not enabled in this build.",
                ExtractedText: null,
                Extractor: null,
                Children: [],
                ExtractionErrorCode: "unsupported_attachment_type");
        }

        if (IsZip(sniffedMimeType, safeFilename))
        {
            // Nested archives share one budget rooted at the email attachment. Resetting
            // limits per child ZIP would allow exponential expansion across the tree.
            var archiveBudget = ArchiveBudget ?? new();
            return ProcessZipContainer(SourceKind, PartId, safeFilename, displayPath, ArchiveEntryPath, MimeType, sniffedMimeType, stored, Content, NestingDepth, CompressedSizeBytes, UncompressedSizeBytes, archiveBudget);
        }

        var extraction = ExtractTerminalText(safeFilename, sniffedMimeType, Content);
        var extractedText = BlankToNull(extraction.Text);
        var ocrText = BlankToNull(extraction.OcrText);
        var extractionStatus = extraction.Status;

        if (extractionStatus == "done" && extractedText is null && ocrText is null)
            extractionStatus = "empty";

        return Node(
            SourceKind,
            PartId,
            safeFilename,
            displayPath,
            ArchiveEntryPath,
            MimeType,
            sniffedMimeType,
            stored.SizeBytes,
            CompressedSizeBytes,
            UncompressedSizeBytes,
            stored.ContentHash,
            stored.StorageKey,
            IsContainer: false,
            NestingDepth,
            DownloadStatus: "stored",
            DownloadError: null,
            ExtractionStatus: extractionStatus,
            ExtractionError: extraction.Error,
            ExtractedText: extractedText,
            Extractor: extraction.Extractor,
            Children: [],
            ExtractionErrorCode: extraction.ErrorCode,
            ExtractorVersion: extraction.ExtractorVersion,
            Exception: extraction.Exception,
            OcrText: ocrText);
    }

    private AttachmentContent ProcessZipContainer(
        string sourceKind,
        string partId,
        string filename,
        string displayPath,
        string archiveEntryPath,
        string mimeType,
        string sniffedMimeType,
        StoredAttachmentObject stored,
        byte[] content,
        int nestingDepth,
        long? compressedSizeBytes,
        long? uncompressedSizeBytes,
        ArchiveProcessingBudget archiveBudget)
    {
        if (nestingDepth >= MaxArchiveDepth)
        {
            return ContainerNode(
                sourceKind,
                partId,
                filename,
                displayPath,
                archiveEntryPath,
                mimeType,
                sniffedMimeType,
                stored,
                nestingDepth,
                compressedSizeBytes,
                uncompressedSizeBytes,
                "failed",
                "Archive nesting depth limit was reached.",
                "archive_safety_limit",
                []);
        }

        try
        {
            using var stream = new MemoryStream(content, writable: false);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
            var children = new List<AttachmentContent>();
            var displayPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var entryOrdinal = 0;

            foreach (var entry in archive.Entries)
            {
                if (archiveBudget.Elapsed > _extractionTimeout)
                    return ContainerNode(sourceKind, partId, filename, displayPath, archiveEntryPath, mimeType, sniffedMimeType, stored, nestingDepth, compressedSizeBytes, uncompressedSizeBytes, "failed", "Archive extraction timed out.", "extractor_timeout", children);

                if (string.IsNullOrWhiteSpace(entry.Name))
                    continue;

                entryOrdinal++;
                if (!archiveBudget.TryCountEntry())
                    return ContainerNode(sourceKind, partId, filename, displayPath, archiveEntryPath, mimeType, sniffedMimeType, stored, nestingDepth, compressedSizeBytes, uncompressedSizeBytes, "failed", "Archive entry count limit was reached.", "archive_safety_limit", children);

                var safeEntryPath = NormalizeArchiveEntryPath(entry.FullName);
                if (safeEntryPath is null)
                {
                    children.Add(RejectedArchiveEntry(
                        displayPath,
                        entry.FullName,
                        displayEntryPath: null,
                        entry.CompressedLength,
                        entry.Length,
                        nestingDepth + 1,
                        entryOrdinal,
                        "Archive entry path was rejected."));
                    continue;
                }
                var displayEntryPath = UniqueArchiveDisplayPath(safeEntryPath, displayPaths);

                if (entry.Length > MaxArchiveEntryBytes)
                {
                    children.Add(RejectedArchiveEntry(
                        displayPath,
                        safeEntryPath,
                        displayEntryPath,
                        entry.CompressedLength,
                        entry.Length,
                        nestingDepth + 1,
                        entryOrdinal,
                        "Archive entry exceeds the per-entry size limit."));
                    continue;
                }

                if (!archiveBudget.TryAddUncompressedBytes(entry.Length))
                    return ContainerNode(sourceKind, partId, filename, displayPath, archiveEntryPath, mimeType, sniffedMimeType, stored, nestingDepth, compressedSizeBytes, uncompressedSizeBytes, "failed", "Archive total uncompressed size limit was reached.", "archive_safety_limit", children);

                if (entry.CompressedLength > 0 && entry.Length / (double)entry.CompressedLength > MaxArchiveCompressionRatio)
                {
                    children.Add(RejectedArchiveEntry(
                        displayPath,
                        safeEntryPath,
                        displayEntryPath,
                        entry.CompressedLength,
                        entry.Length,
                        nestingDepth + 1,
                        entryOrdinal,
                        "Archive entry exceeds the compression-ratio limit."));
                    continue;
                }

                byte[] entryBytes;
                try
                {
                    using var entryStream = entry.Open();
                    using var entryBuffer = new BoundedWriteStream(MaxArchiveEntryBytes);
                    entryStream.CopyTo(entryBuffer);
                    entryBytes = entryBuffer.ToArray();
                }
                catch (AttachmentSizeLimitException)
                {
                    children.Add(RejectedArchiveEntry(
                        displayPath,
                        safeEntryPath,
                        displayEntryPath,
                        entry.CompressedLength,
                        entry.Length,
                        nestingDepth + 1,
                        entryOrdinal,
                        "Archive entry exceeded the per-entry size limit while streaming."));
                    continue;
                }

                if (!archiveBudget.TryAddUncompressedBytes(entryBytes.LongLength - entry.Length))
                    return ContainerNode(sourceKind, partId, filename, displayPath, archiveEntryPath, mimeType, sniffedMimeType, stored, nestingDepth, compressedSizeBytes, uncompressedSizeBytes, "failed", "Archive total uncompressed size limit was reached while streaming.", "archive_safety_limit", children);

                children.Add(ProcessArchiveEntry(
                    displayPath,
                    safeEntryPath,
                    displayEntryPath,
                    Path.GetFileName(safeEntryPath),
                    entry.CompressedLength,
                    entry.Length,
                    entryBytes,
                    nestingDepth + 1,
                    archiveBudget));

                if (archiveBudget.Elapsed > _extractionTimeout)
                    return ContainerNode(sourceKind, partId, filename, displayPath, archiveEntryPath, mimeType, sniffedMimeType, stored, nestingDepth, compressedSizeBytes, uncompressedSizeBytes, "failed", "Archive extraction timed out.", "extractor_timeout", children);
            }

            var hasRejectedEntries = children.Any(child => child.ExtractionErrorCode == "archive_safety_limit");
            return ContainerNode(
                sourceKind,
                partId,
                filename,
                displayPath,
                archiveEntryPath,
                mimeType,
                sniffedMimeType,
                stored,
                nestingDepth,
                compressedSizeBytes,
                uncompressedSizeBytes,
                hasRejectedEntries ? "failed" : children.Count == 0 ? "empty" : "done",
                hasRejectedEntries ? "One or more archive entries were rejected by safety policy." : null,
                hasRejectedEntries ? "archive_safety_limit" : null,
                children);
        }
        catch (InvalidDataException ex)
        {
            return ContainerNode(sourceKind, partId, filename, displayPath, archiveEntryPath, mimeType, sniffedMimeType, stored, nestingDepth, compressedSizeBytes, uncompressedSizeBytes, "failed", "The ZIP archive is invalid or corrupt.", "invalid_document", [], ex);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            var classified = AttachmentFailureClassifier.Classify(ex);
            return ContainerNode(sourceKind, partId, filename, displayPath, archiveEntryPath, mimeType, sniffedMimeType, stored, nestingDepth, compressedSizeBytes, uncompressedSizeBytes, "failed", classified.Summary, classified.ErrorCode, [], ex);
        }
    }

    private static AttachmentContent RejectedArchiveEntry(
        string parentDisplayPath,
        string archiveEntryPath,
        string displayEntryPath,
        long compressedSizeBytes,
        long uncompressedSizeBytes,
        int nestingDepth,
        int entryOrdinal,
        string error)
    {
        var filename = BlankToNull(Path.GetFileName((archiveEntryPath ?? "").Replace('\\', '/'))) ?? "rejected-entry";
        var safeDisplayEntryPath = NormalizeArchiveEntryPath(displayEntryPath)
            ?? NormalizeArchiveEntryPath(archiveEntryPath)
            ?? RejectedArchiveEntryPath(archiveEntryPath, filename, entryOrdinal);
        return Node(
            SourceKind: "archive_entry",
            PartId: null,
            Filename: filename,
            DisplayPath: $"{parentDisplayPath}!/{safeDisplayEntryPath}",
            ArchiveEntryPath: BlankToNull(archiveEntryPath),
            MimeType: GuessMimeType(filename),
            SniffedMimeType: GuessMimeType(filename),
            SizeBytes: uncompressedSizeBytes,
            CompressedSizeBytes: compressedSizeBytes,
            UncompressedSizeBytes: uncompressedSizeBytes,
            ContentHash: null,
            StorageKey: null,
            IsContainer: false,
            NestingDepth: nestingDepth,
            DownloadStatus: "skipped",
            DownloadError: error,
            ExtractionStatus: "failed",
            ExtractionError: error,
            ExtractedText: null,
            Extractor: "ZipArchive",
            Children: [],
            ExtractionErrorCode: "archive_safety_limit",
            ExtractorVersion: ProcessorVersion,
            DownloadErrorCode: "archive_safety_limit");
    }

    private static string UniqueArchiveDisplayPath(
        string entryPath,
        HashSet<string> seen)
    {
        if (seen.Add(entryPath))
            return entryPath;

        var slash = entryPath.LastIndexOf('/');
        var directory = slash >= 0 ? entryPath[..(slash + 1)] : "";
        var filename = slash >= 0 ? entryPath[(slash + 1)..] : entryPath;
        var extension = Path.GetExtension(filename);
        var stem = string.IsNullOrWhiteSpace(extension) ? filename : filename[..^extension.Length];
        var ordinal = 2;
        string candidate;

        do
        {
            candidate = $"{directory}{stem} ({ordinal}){extension}";
            ordinal++;
        }
        while (!seen.Add(candidate));

        return candidate;
    }

    private static string RejectedArchiveEntryPath(string entryPath, string filename, int entryOrdinal)
    {
        var pathHash = Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(entryPath ?? "")))
            .ToLowerInvariant()[..12];
        return $"rejected/{entryOrdinal:D4}-{pathHash}-{filename}";
    }

    private static AttachmentContent ContainerNode(
        string sourceKind,
        string partId,
        string filename,
        string displayPath,
        string archiveEntryPath,
        string mimeType,
        string sniffedMimeType,
        StoredAttachmentObject stored,
        int nestingDepth,
        long? compressedSizeBytes,
        long? uncompressedSizeBytes,
        string extractionStatus,
        string extractionError,
        string extractionErrorCode,
        IReadOnlyList<AttachmentContent> children,
        Exception exception = null)
    {
        return Node(
            sourceKind,
            partId,
            filename,
            displayPath,
            archiveEntryPath,
            mimeType,
            sniffedMimeType,
            stored.SizeBytes,
            compressedSizeBytes,
            uncompressedSizeBytes,
            stored.ContentHash,
            stored.StorageKey,
            IsContainer: true,
            nestingDepth,
            DownloadStatus: "stored",
            DownloadError: null,
            ExtractionStatus: extractionStatus,
            ExtractionError: extractionError,
            ExtractedText: null,
            Extractor: "ZipArchive",
            Children: children,
            ExtractionErrorCode: extractionErrorCode,
            ExtractorVersion: ProcessorVersion,
            Exception: exception);
    }

    private static AttachmentContent Node(
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
        string Extractor,
        IReadOnlyList<AttachmentContent> Children,
        string ExtractionErrorCode = null,
        string ExtractorVersion = null,
        Exception Exception = null,
        string DownloadErrorCode = null,
        string OcrText = null)
    {
        return new(
            SourceKind,
            PartId,
            Filename,
            DisplayPath,
            ArchiveEntryPath,
            MimeType,
            SniffedMimeType,
            SizeBytes,
            CompressedSizeBytes,
            UncompressedSizeBytes,
            ContentHash,
            StorageKey,
            IsContainer,
            NestingDepth,
            DownloadStatus,
            DownloadError,
            ExtractionStatus,
            ExtractionError,
            ExtractedText,
            OcrText,
            Extractor,
            Children ?? [],
            ExtractionErrorCode,
            ExtractorVersion,
            Exception?.GetType().FullName,
            Exception?.Message,
            Exception?.ToString(),
            DownloadErrorCode);
    }

    private TerminalExtraction ExtractTerminalText(string filename, string mimeType, byte[] content)
    {
        // Built-in document libraries expose synchronous extraction and cannot be
        // forcibly canceled. Run them off-thread to bound the caller's wait while
        // keeping at most one runaway leaf-document extraction alive.
        if (!TerminalExtractionGate.Wait(0))
            return Failure("extractor_unavailable", "The attachment extractor is still busy with timed-out work.");

        var extractionTask = Task.Run(() => ExtractTerminalTextCore(filename, mimeType, content));
        try
        {
            var result = extractionTask
                .WaitAsync(_extractionTimeout)
                .GetAwaiter()
                .GetResult();
            TerminalExtractionGate.Release();
            return result;
        }
        catch (TimeoutException)
        {
            _ = extractionTask.ContinueWith(
                completed =>
                {
                    _ = completed.Exception;
                    TerminalExtractionGate.Release();
                },
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
            return Failure("extractor_timeout", "Attachment extraction timed out.");
        }
        catch
        {
            TerminalExtractionGate.Release();
            throw;
        }
    }

    private TerminalExtraction ExtractTerminalTextCore(string filename, string mimeType, byte[] content)
    {
        try
        {
            if (_terminalExtractor is not null)
            {
                var output = _terminalExtractor(new(filename, mimeType, content));
                return Success(output.Text, output.Extractor, output.ExtractorVersion);
            }

            if (IsPdf(mimeType, filename))
                return ExtractPdf(content);

            if (IsDocx(mimeType, filename))
                return ExtractDocx(content);

            if (IsXlsx(mimeType, filename))
                return ExtractXlsx(content);

            if (IsHtml(mimeType, filename))
                return Success(NormalizeText(HtmlToText(ReadText(content))), "html");

            if (IsPlainText(mimeType, filename) || IsCsv(mimeType, filename))
                return Success(NormalizeText(ReadText(content)), IsCsv(mimeType, filename) ? "csv" : "text");

            return Failure(
                "unsupported_attachment_type",
                $"No text extractor is available for {BlankToNull(mimeType) ?? "this attachment type"}.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            var classified = AttachmentFailureClassifier.Classify(ex);
            return Failure(classified.ErrorCode, classified.Summary, ex);
        }
    }

    private TerminalExtraction ExtractPdf(byte[] content)
    {
        using var document = PdfDocument.Open(content);
        var builder = new StringBuilder();
        var ocrCandidatePages = new List<int>();
        var pageIndex = 0;

        foreach (var page in document.GetPages())
        {
            AppendBounded(builder, page.Text);
            if (IsPdfOcrCandidate(page.Text))
                ocrCandidatePages.Add(pageIndex);
            pageIndex++;
        }

        var embeddedText = NormalizeText(builder.ToString());
        if (ocrCandidatePages.Count == 0)
            return Success(embeddedText, "PdfPig");

        if (!_ocrConfig.Enabled)
        {
            return Failure(
                "ocr_disabled",
                "The PDF needs OCR, but OCR is disabled in config.toml.",
                text: embeddedText,
                extractor: "PdfPig");
        }

        var ocr = _pdfOcrExtractor.Extract(content, ocrCandidatePages);
        var extractor = string.IsNullOrWhiteSpace(ocr.ExtractorVersion)
            ? $"PdfPig+Tesseract({ocr.Model})"
            : $"PdfPig+Tesseract({ocr.Model}; {ocr.ExtractorVersion})";
        return Success(
            embeddedText,
            extractor,
            ProcessorVersion,
            NormalizeText(ocr.Text));
    }

    private static bool IsPdfOcrCandidate(string pageText)
    {
        if (string.IsNullOrWhiteSpace(pageText))
            return true;

        var meaningful = pageText.Count(char.IsLetterOrDigit);
        var suspicious = pageText.Count(ch => ch == '\uFFFD' || char.IsControl(ch) && !char.IsWhiteSpace(ch));
        return meaningful < MinUsefulPdfPageCharacters
            || suspicious > 0 && suspicious * 4 > Math.Max(1, meaningful);
    }

    private static TerminalExtraction ExtractDocx(byte[] content)
    {
        using var stream = new MemoryStream(content, writable: false);
        using var document = WordprocessingDocument.Open(stream, false);
        return Success(NormalizeText(document.MainDocumentPart?.Document?.Body?.InnerText), "DocumentFormat.OpenXml");
    }

    private static TerminalExtraction ExtractXlsx(byte[] content)
    {
        using var stream = new MemoryStream(content, writable: false);
        using var document = SpreadsheetDocument.Open(stream, false);
        var builder = new StringBuilder();
        var sharedStrings = document.WorkbookPart?.SharedStringTablePart?.SharedStringTable
            ?.Elements<SharedStringItem>()
            .Select(item => item.InnerText)
            .ToList() ?? [];

        foreach (var worksheetPart in document.WorkbookPart?.WorksheetParts ?? [])
        {
            foreach (var cell in worksheetPart.Worksheet.Descendants<Cell>())
                AppendBounded(builder, ReadCellValue(cell, sharedStrings));
        }

        return Success(NormalizeText(builder.ToString()), "DocumentFormat.OpenXml");
    }

    private static string ReadCellValue(Cell cell, IReadOnlyList<string> sharedStrings)
    {
        // XLSX stores many cell strings as indexes into a workbook-wide shared-string
        // table; other cell types keep their displayable value directly in the cell.
        var raw = cell.CellValue?.Text ?? cell.InnerText;
        if (string.IsNullOrWhiteSpace(raw))
            return null;

        if (cell.DataType?.Value == CellValues.SharedString
            && int.TryParse(raw, out var index)
            && index >= 0
            && index < sharedStrings.Count)
            return sharedStrings[index];

        return raw;
    }

    private static void AppendBounded(StringBuilder builder, string value)
    {
        value = BlankToNull(value);
        if (value is null || builder.Length >= MaxExtractedChars)
            return;

        if (builder.Length > 0)
            builder.AppendLine();

        var remaining = MaxExtractedChars - builder.Length;
        if (remaining <= 0)
            return;

        builder.Append(value.Length <= remaining ? value : value[..remaining]);
    }

    private static string ReadText(byte[] content)
    {
        if (content.Length >= 3 && content[0] == 0xEF && content[1] == 0xBB && content[2] == 0xBF)
            return Encoding.UTF8.GetString(content, 3, content.Length - 3);

        if (content.Length >= 2 && content[0] == 0xFF && content[1] == 0xFE)
            return Encoding.Unicode.GetString(content, 2, content.Length - 2);

        if (content.Length >= 2 && content[0] == 0xFE && content[1] == 0xFF)
            return Encoding.BigEndianUnicode.GetString(content, 2, content.Length - 2);

        return Encoding.UTF8.GetString(content);
    }

    private static string HtmlToText(string html)
    {
        if (string.IsNullOrWhiteSpace(html))
            return null;

        var withoutScripts = ScriptOrStyleRegex.Replace(html, " ");
        var withBreaks = BlockBreakRegex.Replace(withoutScripts, "\n");
        var withoutTags = TagRegex.Replace(withBreaks, " ");
        return WebUtility.HtmlDecode(withoutTags);
    }

    private static string NormalizeText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var normalized = LineBreakRegex.Replace(text.Replace("\r\n", "\n"), "\n");
        normalized = HorizontalWhitespaceRegex.Replace(normalized, " ");
        normalized = ExcessiveBlankLinesRegex.Replace(normalized, "\n\n").Trim();

        if (normalized.Length > MaxExtractedChars)
            normalized = normalized[..MaxExtractedChars];

        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    private static string SniffMimeType(string filename, string declaredMimeType, byte[] content)
    {
        var extensionMimeType = GuessMimeType(filename);

        if (IsDocx(extensionMimeType, filename) || IsXlsx(extensionMimeType, filename))
            return extensionMimeType;

        if (content is { Length: >= 5 }
            && content[0] == '%'
            && content[1] == 'P'
            && content[2] == 'D'
            && content[3] == 'F'
            && content[4] == '-')
            return "application/pdf";

        if (content is { Length: >= 4 }
            && content[0] == 0x50
            && content[1] == 0x4B
            && content[2] is 0x03 or 0x05 or 0x07
            && content[3] is 0x04 or 0x06 or 0x08)
            return extensionMimeType is "application/vnd.openxmlformats-officedocument.wordprocessingml.document"
                or "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet"
                ? extensionMimeType
                : "application/zip";

        if (content is { Length: >= 6 }
            && content[0] == 0x37
            && content[1] == 0x7A
            && content[2] == 0xBC
            && content[3] == 0xAF
            && content[4] == 0x27
            && content[5] == 0x1C)
            return "application/x-7z-compressed";

        return BlankToNull(declaredMimeType) ?? extensionMimeType;
    }

    private static string GuessMimeType(string filename)
    {
        var extension = Path.GetExtension(filename ?? "").ToLowerInvariant();
        return extension switch
        {
            ".pdf" => "application/pdf",
            ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            ".txt" => "text/plain",
            ".csv" => "text/csv",
            ".htm" or ".html" => "text/html",
            ".zip" => "application/zip",
            ".7z" => "application/x-7z-compressed",
            _ => "application/octet-stream"
        };
    }

    private static string NormalizeArchiveEntryPath(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var normalized = value.Replace('\\', '/').Trim();
        if (normalized.Length == 0
            || normalized.StartsWith("/", StringComparison.Ordinal)
            || normalized.StartsWith("~", StringComparison.Ordinal)
            || Regex.IsMatch(normalized, "^[A-Za-z]:"))
            return null;

        normalized = normalized.TrimEnd('/');

        var parts = normalized
            .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

        if (parts.Count == 0 || parts.Any(part => part is "." or ".." || part.Contains(':')))
            return null;

        return string.Join("/", parts);
    }

    private static bool IsPdf(string mimeType, string filename) =>
        mimeType.Equals("application/pdf", StringComparison.OrdinalIgnoreCase)
        || Path.GetExtension(filename).Equals(".pdf", StringComparison.OrdinalIgnoreCase);

    private static bool IsDocx(string mimeType, string filename) =>
        mimeType.Equals("application/vnd.openxmlformats-officedocument.wordprocessingml.document", StringComparison.OrdinalIgnoreCase)
        || Path.GetExtension(filename).Equals(".docx", StringComparison.OrdinalIgnoreCase);

    private static bool IsXlsx(string mimeType, string filename) =>
        mimeType.Equals("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", StringComparison.OrdinalIgnoreCase)
        || Path.GetExtension(filename).Equals(".xlsx", StringComparison.OrdinalIgnoreCase);

    private static bool IsHtml(string mimeType, string filename) =>
        mimeType.StartsWith("text/html", StringComparison.OrdinalIgnoreCase)
        || Path.GetExtension(filename).Equals(".html", StringComparison.OrdinalIgnoreCase)
        || Path.GetExtension(filename).Equals(".htm", StringComparison.OrdinalIgnoreCase);

    private static bool IsCsv(string mimeType, string filename) =>
        mimeType.StartsWith("text/csv", StringComparison.OrdinalIgnoreCase)
        || Path.GetExtension(filename).Equals(".csv", StringComparison.OrdinalIgnoreCase);

    private static bool IsPlainText(string mimeType, string filename) =>
        mimeType.StartsWith("text/plain", StringComparison.OrdinalIgnoreCase)
        || Path.GetExtension(filename).Equals(".txt", StringComparison.OrdinalIgnoreCase);

    private static bool IsZip(string mimeType, string filename) =>
        mimeType.Equals("application/zip", StringComparison.OrdinalIgnoreCase)
        || Path.GetExtension(filename).Equals(".zip", StringComparison.OrdinalIgnoreCase);

    private static bool IsSevenZip(string mimeType, string filename) =>
        mimeType.Equals("application/x-7z-compressed", StringComparison.OrdinalIgnoreCase)
        || Path.GetExtension(filename).Equals(".7z", StringComparison.OrdinalIgnoreCase);

    private static string BlankToNull(string value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static TerminalExtraction Success(
        string text,
        string extractor,
        string extractorVersion = ProcessorVersion,
        string ocrText = null) =>
        new("done", text, ocrText, null, null, extractor, extractorVersion, null);

    private static TerminalExtraction Failure(
        string errorCode,
        string error,
        Exception exception = null,
        string text = null,
        string extractor = null) =>
        new("failed", text, null, error, errorCode, extractor, ProcessorVersion, exception);

    // Result of extracting one non-container ("terminal") document. Archive trees
    // use AttachmentContent instead because they can produce child attachment nodes.
    private sealed record TerminalExtraction(
        string Status,
        string Text,
        string OcrText,
        string Error,
        string ErrorCode,
        string Extractor,
        string ExtractorVersion,
        Exception Exception);

    // One budget follows the entire recursively expanded archive tree. It bounds
    // aggregate work rather than allowing each nested ZIP to restart the limits.
    private sealed class ArchiveProcessingBudget
    {
        private readonly Stopwatch _elapsed = Stopwatch.StartNew();
        private long _uncompressedBytes;
        private int _entryCount;

        public TimeSpan Elapsed => _elapsed.Elapsed;

        public bool TryCountEntry()
        {
            _entryCount++;
            return _entryCount <= MaxArchiveEntries;
        }

        public bool TryAddUncompressedBytes(long bytes)
        {
            if (bytes <= 0)
            {
                _uncompressedBytes = Math.Max(0, _uncompressedBytes + bytes);
                return true;
            }

            if (_uncompressedBytes > MaxArchiveTotalUncompressedBytes - bytes)
                return false;

            _uncompressedBytes += bytes;
            return true;
        }
    }

    private static readonly Regex ScriptOrStyleRegex = new(
        @"<(script|style)\b[^>]*>.*?</\1>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex BlockBreakRegex = new(
        @"</?(br|p|div|li|tr|table|h[1-6])\b[^>]*>",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex TagRegex = new(
        "<[^>]+>",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex LineBreakRegex = new(
        @"[ \t]*\n[ \t]*",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex HorizontalWhitespaceRegex = new(
        @"[^\S\n]+",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex ExcessiveBlankLinesRegex = new(
        @"\n{3,}",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
}
