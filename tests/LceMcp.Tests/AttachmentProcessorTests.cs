using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using DocumentFormat.OpenXml.Wordprocessing;
using System.IO.Compression;
using System.Text;

namespace LceMcp.Tests;

[Collection("Process-wide attachment extraction gate")]
public sealed class AttachmentProcessorTests
{
    [Fact]
    public void ZipExpansionRejectsTraversalAndExtractsSafeHtmlChild()
    {
        using var temp = TempWorkspace.Create();
        var processor = new AttachmentProcessor(new AttachmentObjectStore(temp.Paths));
        var zipBytes = CreateZip(new Dictionary<string, string>
        {
            ["docs/invoice.HTML"] = "<html><body><p>Invoice VAT and DDV total.</p></body></html>",
            ["evil.txt"] = "safe sibling",
            ["../evil.txt"] = "should not appear",
            ["C:/absolute.txt"] = "should not appear either",
            ["/absolute-unix.txt"] = "should not appear either"
        });

        var attachment = processor.ProcessEmailAttachment(
            partId: "2",
            filename: "bundle.zip",
            mimeType: "application/zip",
            declaredSizeBytes: zipBytes.Length,
            content: zipBytes,
            displayPath: "bundle.zip");

        var child = Assert.Single(attachment.Children, item => item.DisplayPath.EndsWith("docs/invoice.HTML"));
        var rejected = attachment.Children.Where(item => item.DownloadStatus == "skipped").ToList();
        Assert.True(attachment.IsContainer);
        Assert.Equal("failed", attachment.ExtractionStatus);
        Assert.Equal("archive_safety_limit", attachment.ExtractionErrorCode);
        Assert.Equal(3, rejected.Count);
        Assert.All(rejected, item => Assert.Equal("archive_safety_limit", item.ExtractionErrorCode));
        Assert.All(rejected, item => Assert.Contains("!/rejected/", item.DisplayPath));
        Assert.Equal(
            attachment.Children.Count,
            attachment.Children.Select(item => item.DisplayPath).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal("bundle.zip!/docs/invoice.HTML", child.DisplayPath);
        Assert.Equal("done", child.ExtractionStatus);
        Assert.Contains("DDV", child.ExtractedText, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("evil", child.ExtractedText ?? "", StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DocxAndXlsxAreTerminalDocumentsNotArchiveChildren()
    {
        using var temp = TempWorkspace.Create();
        var processor = new AttachmentProcessor(new AttachmentObjectStore(temp.Paths));
        var docx = processor.ProcessEmailAttachment(
            partId: "2",
            filename: "invoice.docx",
            mimeType: "application/octet-stream",
            declaredSizeBytes: null,
            content: CreateDocx("DOCX invoice DDV line"),
            displayPath: "invoice.docx");
        var xlsx = processor.ProcessEmailAttachment(
            partId: "3",
            filename: "invoice.xlsx",
            mimeType: "application/octet-stream",
            declaredSizeBytes: null,
            content: CreateXlsx("XLSX VAT total"),
            displayPath: "invoice.xlsx");

        Assert.False(docx.IsContainer);
        Assert.Empty(docx.Children);
        Assert.Equal("done", docx.ExtractionStatus);
        Assert.Contains("DDV", docx.ExtractedText, StringComparison.OrdinalIgnoreCase);
        Assert.False(xlsx.IsContainer);
        Assert.Empty(xlsx.Children);
        Assert.Equal("done", xlsx.ExtractionStatus);
        Assert.Contains("VAT", xlsx.ExtractedText, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CorruptTerminalDocumentIsStoredAndMarkedFailed()
    {
        using var temp = TempWorkspace.Create();
        var processor = new AttachmentProcessor(new AttachmentObjectStore(temp.Paths));

        var attachment = processor.ProcessEmailAttachment(
            partId: "2",
            filename: "broken.docx",
            mimeType: "application/octet-stream",
            declaredSizeBytes: null,
            content: Encoding.UTF8.GetBytes("not a real docx"),
            displayPath: "broken.docx");

        Assert.False(attachment.IsContainer);
        Assert.Equal("stored", attachment.DownloadStatus);
        Assert.Equal("failed", attachment.ExtractionStatus);
        Assert.Equal("invalid_document", attachment.ExtractionErrorCode);
        Assert.NotNull(attachment.StorageKey);
        Assert.Null(attachment.ExtractedText);
    }

    [Fact]
    public void DuplicateArchiveEntryNamesReceiveUniqueDisplayPaths()
    {
        using var temp = TempWorkspace.Create();
        var processor = new AttachmentProcessor(new AttachmentObjectStore(temp.Paths));
        var zipBytes = CreateZip(
        [
            new("report.txt", "first"),
            new("report.txt", "second"),
            new("report (2).txt", "third")
        ]);

        var attachment = processor.ProcessEmailAttachment(
            partId: "2",
            filename: "duplicates.zip",
            mimeType: "application/zip",
            declaredSizeBytes: zipBytes.Length,
            content: zipBytes,
            displayPath: "duplicates.zip");

        Assert.Equal(3, attachment.Children.Count);
        Assert.Equal(
            attachment.Children.Count,
            attachment.Children.Select(item => item.DisplayPath).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Contains(attachment.Children, item => item.DisplayPath == "duplicates.zip!/report.txt");
        Assert.Contains(attachment.Children, item => item.DisplayPath == "duplicates.zip!/report (2).txt");
        Assert.Contains(attachment.Children, item => item.DisplayPath == "duplicates.zip!/report (2) (2).txt");
    }

    [Fact]
    public void NestedZipExpansionSharesOneRootEntryBudget()
    {
        using var temp = TempWorkspace.Create();
        var processor = new AttachmentProcessor(new AttachmentObjectStore(temp.Paths));
        var first = CreateZip(Enumerable.Range(1, 100).ToDictionary(
            index => $"first/{index}.txt",
            index => $"first {index}"));
        var second = CreateZip(Enumerable.Range(1, 100).ToDictionary(
            index => $"second/{index}.txt",
            index => $"second {index}"));
        var outer = CreateBinaryZip(new Dictionary<string, byte[]>
        {
            ["first.zip"] = first,
            ["second.zip"] = second
        });

        var attachment = processor.ProcessEmailAttachment(
            partId: "2",
            filename: "nested.zip",
            mimeType: "application/zip",
            declaredSizeBytes: outer.Length,
            content: outer,
            displayPath: "nested.zip");

        Assert.Equal("failed", attachment.ExtractionStatus);
        Assert.Equal("archive_safety_limit", attachment.ExtractionErrorCode);
        Assert.Contains(
            attachment.Children,
            child => child.ExtractionErrorCode == "archive_safety_limit");
    }

    private static byte[] CreateZip(IEnumerable<KeyValuePair<string, string>> entries)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var entry in entries)
            {
                var zipEntry = archive.CreateEntry(entry.Key);
                using var writer = new StreamWriter(zipEntry.Open(), Encoding.UTF8);
                writer.Write(entry.Value);
            }
        }

        return stream.ToArray();
    }

    private static byte[] CreateBinaryZip(IEnumerable<KeyValuePair<string, byte[]>> entries)
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var entry in entries)
            {
                var zipEntry = archive.CreateEntry(entry.Key);
                using var entryStream = zipEntry.Open();
                entryStream.Write(entry.Value);
            }
        }

        return stream.ToArray();
    }

    private static byte[] CreateDocx(string text)
    {
        using var stream = new MemoryStream();
        using (var document = WordprocessingDocument.Create(stream, WordprocessingDocumentType.Document, autoSave: true))
        {
            var main = document.AddMainDocumentPart();
            main.Document = new Document(new Body(new Paragraph(new DocumentFormat.OpenXml.Wordprocessing.Run(new DocumentFormat.OpenXml.Wordprocessing.Text(text)))));
            main.Document.Save();
        }

        return stream.ToArray();
    }

    private static byte[] CreateXlsx(string text)
    {
        using var stream = new MemoryStream();
        using (var document = SpreadsheetDocument.Create(stream, SpreadsheetDocumentType.Workbook, autoSave: true))
        {
            var workbookPart = document.AddWorkbookPart();
            workbookPart.Workbook = new Workbook();
            var worksheetPart = workbookPart.AddNewPart<WorksheetPart>();
            worksheetPart.Worksheet = new Worksheet(new SheetData(new Row(new Cell
            {
                DataType = CellValues.String,
                CellValue = new CellValue(text)
            })));

            workbookPart.Workbook.AppendChild(new Sheets(new Sheet
            {
                Id = workbookPart.GetIdOfPart(worksheetPart),
                SheetId = 1,
                Name = "Sheet1"
            }));
            workbookPart.Workbook.Save();
        }

        return stream.ToArray();
    }
}
