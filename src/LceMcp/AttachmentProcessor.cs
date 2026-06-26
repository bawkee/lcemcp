using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using UglyToad.PdfPig;

namespace LceMcp;

internal sealed class AttachmentProcessor
{
    private const long MaxAttachmentBytes = 25L * 1024 * 1024;
    private const int MaxExtractedChars = 1_000_000;
    private const int MaxArchiveDepth = 3;
    private const int MaxArchiveEntries = 200;
    private const long MaxArchiveEntryBytes = 25L * 1024 * 1024;
    private const long MaxArchiveTotalUncompressedBytes = 100L * 1024 * 1024;
    private const double MaxArchiveCompressionRatio = 100d;

    private readonly AttachmentObjectStore _objectStore;

    public AttachmentProcessor(AttachmentObjectStore objectStore)
    {
        _objectStore = objectStore;
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

    private AttachmentContent ProcessArchiveEntry(
        string parentDisplayPath,
        string archiveEntryPath,
        string filename,
        long compressedSizeBytes,
        long uncompressedSizeBytes,
        byte[] content,
        int nestingDepth)
    {
        return ProcessStoredAttachment(
            SourceKind: "archive_entry",
            PartId: null,
            Filename: filename,
            DisplayPath: $"{parentDisplayPath}!/{archiveEntryPath}",
            ArchiveEntryPath: archiveEntryPath,
            MimeType: GuessMimeType(filename),
            DeclaredSizeBytes: uncompressedSizeBytes,
            CompressedSizeBytes: compressedSizeBytes,
            UncompressedSizeBytes: uncompressedSizeBytes,
            Content: content,
            NestingDepth: nestingDepth);
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
        int NestingDepth)
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
                ExtractionStatus: "not_ready",
                ExtractionError: null,
                ExtractedText: null,
                Extractor: null,
                Children: []);
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
                ExtractionStatus: "too_large",
                ExtractionError: null,
                ExtractedText: null,
                Extractor: null,
                Children: []);
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
                ExtractionStatus: "unsupported",
                ExtractionError: "7z archive expansion is not enabled in this build.",
                ExtractedText: null,
                Extractor: null,
                Children: []);
        }

        if (IsZip(sniffedMimeType, safeFilename))
            return ProcessZipContainer(SourceKind, PartId, safeFilename, displayPath, ArchiveEntryPath, MimeType, sniffedMimeType, stored, Content, NestingDepth, CompressedSizeBytes, UncompressedSizeBytes);

        var extraction = ExtractTerminalText(safeFilename, sniffedMimeType, Content);
        var extractedText = BlankToNull(extraction.Text);
        var extractionStatus = extraction.Status;

        if (extractionStatus == "done" && extractedText is null)
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
            Children: []);
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
        long? uncompressedSizeBytes)
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
                "too_large",
                "Archive nesting depth limit was reached.",
                []);
        }

        try
        {
            using var stream = new MemoryStream(content, writable: false);
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false);
            var children = new List<AttachmentContent>();
            long totalUncompressed = 0;
            var entryCount = 0;

            foreach (var entry in archive.Entries)
            {
                if (string.IsNullOrWhiteSpace(entry.Name))
                    continue;

                entryCount++;
                if (entryCount > MaxArchiveEntries)
                    return ContainerNode(sourceKind, partId, filename, displayPath, archiveEntryPath, mimeType, sniffedMimeType, stored, nestingDepth, compressedSizeBytes, uncompressedSizeBytes, "too_large", "Archive entry count limit was reached.", children);

                var safeEntryPath = NormalizeArchiveEntryPath(entry.FullName);
                if (safeEntryPath is null)
                    continue;

                if (entry.Length > MaxArchiveEntryBytes)
                    continue;

                totalUncompressed += entry.Length;
                if (totalUncompressed > MaxArchiveTotalUncompressedBytes)
                    return ContainerNode(sourceKind, partId, filename, displayPath, archiveEntryPath, mimeType, sniffedMimeType, stored, nestingDepth, compressedSizeBytes, uncompressedSizeBytes, "too_large", "Archive total uncompressed size limit was reached.", children);

                if (entry.CompressedLength > 0 && entry.Length / (double)entry.CompressedLength > MaxArchiveCompressionRatio)
                    continue;

                using var entryStream = entry.Open();
                using var entryBuffer = new MemoryStream();
                entryStream.CopyTo(entryBuffer);
                children.Add(ProcessArchiveEntry(
                    displayPath,
                    safeEntryPath,
                    Path.GetFileName(safeEntryPath),
                    entry.CompressedLength,
                    entry.Length,
                    entryBuffer.ToArray(),
                    nestingDepth + 1));
            }

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
                children.Count == 0 ? "empty" : "done",
                null,
                children);
        }
        catch (InvalidDataException ex)
        {
            return ContainerNode(sourceKind, partId, filename, displayPath, archiveEntryPath, mimeType, sniffedMimeType, stored, nestingDepth, compressedSizeBytes, uncompressedSizeBytes, "failed", ex.Message, []);
        }
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
        IReadOnlyList<AttachmentContent> children)
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
            Children: children);
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
        IReadOnlyList<AttachmentContent> Children)
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
            OcrText: null,
            Extractor,
            Children ?? []);
    }

    private static TerminalExtraction ExtractTerminalText(string filename, string mimeType, byte[] content)
    {
        try
        {
            if (IsPdf(mimeType, filename))
                return ExtractPdf(content);

            if (IsDocx(mimeType, filename))
                return ExtractDocx(content);

            if (IsXlsx(mimeType, filename))
                return ExtractXlsx(content);

            if (IsHtml(mimeType, filename))
                return new("done", NormalizeText(HtmlToText(ReadText(content))), null, "html");

            if (IsPlainText(mimeType, filename) || IsCsv(mimeType, filename))
                return new("done", NormalizeText(ReadText(content)), null, IsCsv(mimeType, filename) ? "csv" : "text");

            return new("unsupported", null, null, null);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or ArgumentException or NotSupportedException)
        {
            return new("failed", null, ex.Message, null);
        }
    }

    private static TerminalExtraction ExtractPdf(byte[] content)
    {
        using var document = PdfDocument.Open(content);
        var builder = new StringBuilder();

        foreach (var page in document.GetPages())
            AppendBounded(builder, page.Text);

        return new("done", builder.ToString(), null, "PdfPig");
    }

    private static TerminalExtraction ExtractDocx(byte[] content)
    {
        using var stream = new MemoryStream(content, writable: false);
        using var document = WordprocessingDocument.Open(stream, false);
        return new("done", NormalizeText(document.MainDocumentPart?.Document?.Body?.InnerText), null, "DocumentFormat.OpenXml");
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

        return new("done", NormalizeText(builder.ToString()), null, "DocumentFormat.OpenXml");
    }

    private static string ReadCellValue(Cell cell, IReadOnlyList<string> sharedStrings)
    {
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

        var remaining = MaxExtractedChars - builder.Length;
        if (builder.Length > 0)
            builder.AppendLine();

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

        var normalized = value.Replace('\\', '/').Trim('/');
        if (normalized.Length == 0
            || normalized.StartsWith("/", StringComparison.Ordinal)
            || normalized.StartsWith("~", StringComparison.Ordinal)
            || Regex.IsMatch(normalized, "^[A-Za-z]:"))
            return null;

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
        || Path.GetExtension(filename) is ".html" or ".htm";

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

    private sealed record TerminalExtraction(
        string Status,
        string Text,
        string Error,
        string Extractor);

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
