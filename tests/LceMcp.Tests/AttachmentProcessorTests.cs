using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using DocumentFormat.OpenXml.Wordprocessing;
using System.IO.Compression;
using System.Text;

namespace LceMcp.Tests;

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
            ["../evil.txt"] = "should not appear",
            ["C:/absolute.txt"] = "should not appear either"
        });

        var attachment = processor.ProcessEmailAttachment(
            partId: "2",
            filename: "bundle.zip",
            mimeType: "application/zip",
            declaredSizeBytes: zipBytes.Length,
            content: zipBytes,
            displayPath: "bundle.zip");

        var child = Assert.Single(attachment.Children);
        Assert.True(attachment.IsContainer);
        Assert.Equal("done", attachment.ExtractionStatus);
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
        Assert.NotNull(attachment.StorageKey);
        Assert.Null(attachment.ExtractedText);
    }

    private static byte[] CreateZip(IReadOnlyDictionary<string, string> entries)
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
