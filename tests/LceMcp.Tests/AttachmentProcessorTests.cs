using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using DocumentFormat.OpenXml.Wordprocessing;
using SkiaSharp;
using System.IO.Compression;
using System.Text;

namespace LceMcp.Tests;

[Collection("Process-wide attachment extraction gate")]
public sealed class AttachmentProcessorTests
{
    [Fact]
    public void ImageOnlyPdfFeedsOcrTextIntoNormalAttachmentResult()
    {
        using var temp = TempWorkspace.Create();
        var ocr = new StubPdfOcrExtractor(
            new("Scanned invoice total 123.45", "Latin", "test-ocr"));
        var processor = new AttachmentProcessor(
            new AttachmentObjectStore(temp.Paths),
            ocrConfig: new() { Enabled = true },
            pdfOcrExtractor: ocr);
        var pdf = CreateImageOnlyPdf("SCANNED INVOICE TOTAL 123.45");

        var attachment = processor.ProcessEmailAttachment(
            "2",
            "scan.pdf",
            "application/pdf",
            pdf.Length,
            pdf,
            "scan.pdf");

        Assert.Equal("done", attachment.ExtractionStatus);
        Assert.Null(attachment.ExtractedText);
        Assert.Contains("invoice total", attachment.OcrText, StringComparison.OrdinalIgnoreCase);
        Assert.Equal([0], ocr.LastPageIndexes);
        Assert.Contains("Tesseract", attachment.Extractor);
        Assert.Contains("test-ocr", attachment.Extractor);
        Assert.Equal(AttachmentProcessor.ProcessorVersion, attachment.ExtractorVersion);
    }

    [Fact]
    public void PdfOcrCandidateIsExplicitFailureWhenOcrIsDisabled()
    {
        using var temp = TempWorkspace.Create();
        var processor = new AttachmentProcessor(new AttachmentObjectStore(temp.Paths));
        var pdf = CreateImageOnlyPdf("OCR IS DISABLED");

        var attachment = processor.ProcessEmailAttachment(
            "2",
            "scan.pdf",
            "application/pdf",
            pdf.Length,
            pdf,
            "scan.pdf");

        Assert.Equal("failed", attachment.ExtractionStatus);
        Assert.Equal("ocr_disabled", attachment.ExtractionErrorCode);
        Assert.Null(attachment.OcrText);
        Assert.Equal("PdfPig", attachment.Extractor);
    }

    [Fact]
    public void PdfOcrWorkerTimeoutUsesExistingFailureClassification()
    {
        using var temp = TempWorkspace.Create();
        var processor = new AttachmentProcessor(
            new AttachmentObjectStore(temp.Paths),
            ocrConfig: new() { Enabled = true },
            pdfOcrExtractor: new ThrowingPdfOcrExtractor(new TimeoutException("test timeout")));
        var pdf = CreateImageOnlyPdf("OCR TIMEOUT");

        var attachment = processor.ProcessEmailAttachment(
            "2",
            "scan.pdf",
            "application/pdf",
            pdf.Length,
            pdf,
            "scan.pdf");

        Assert.Equal("failed", attachment.ExtractionStatus);
        Assert.Equal("extractor_timeout", attachment.ExtractionErrorCode);
        Assert.NotNull(attachment.ExceptionDetails);
    }

    [Fact]
    public void PdfOcrRejectsMoreThanBoundedCandidatePageCount()
    {
        using var temp = TempWorkspace.Create();
        var config = new OcrConfig { Enabled = true };
        config.Languages.Add("eng");
        var processor = new AttachmentProcessor(
            new AttachmentObjectStore(temp.Paths),
            ocrConfig: config);
        var pdf = CreateImageOnlyPdf("TOO MANY OCR PAGES", PdfOcrExtractor.MaxOcrPages + 1);

        var attachment = processor.ProcessEmailAttachment(
            "2",
            "large-scan.pdf",
            "application/pdf",
            pdf.Length,
            pdf,
            "large-scan.pdf");

        Assert.Equal("failed", attachment.ExtractionStatus);
        Assert.Equal("ocr_safety_limit", attachment.ExtractionErrorCode);
        Assert.Empty(Directory.EnumerateFiles(temp.Paths.TessdataDirectory, "*.traineddata"));
    }

    [Fact]
    public void MixedPdfOnlySendsPagesWithoutUsefulEmbeddedTextToOcr()
    {
        using var temp = TempWorkspace.Create();
        var ocr = new StubPdfOcrExtractor(
            new("OCR text from the scanned second page", "Latin", "test-ocr"));
        var processor = new AttachmentProcessor(
            new AttachmentObjectStore(temp.Paths),
            ocrConfig: new() { Enabled = true },
            pdfOcrExtractor: ocr);
        var pdf = CreateMixedPdf();

        var attachment = processor.ProcessEmailAttachment(
            "2",
            "mixed.pdf",
            "application/pdf",
            pdf.Length,
            pdf,
            "mixed.pdf");

        Assert.Equal("done", attachment.ExtractionStatus);
        Assert.Contains("selectable", attachment.ExtractedText, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("scanned second", attachment.OcrText, StringComparison.OrdinalIgnoreCase);
        Assert.Equal([1], ocr.LastPageIndexes);
    }

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

    private static byte[] CreateImageOnlyPdf(string text, int pageCount = 1)
    {
        using var bitmap = new SKBitmap(1_200, 1_600);
        using (var canvas = new SKCanvas(bitmap))
        using (var font = new SKFont(SKTypeface.Default, 52))
        using (var paint = new SKPaint { Color = SKColors.Black, IsAntialias = true })
        {
            canvas.Clear(SKColors.White);
            canvas.DrawText(text, 80, 180, font, paint);
            canvas.DrawText("This page contains raster pixels, not a PDF text layer.", 80, 280, font, paint);
        }

        using var stream = new MemoryStream();
        using (var document = SKDocument.CreatePdf(stream))
        {
            for (var pageIndex = 0; pageIndex < pageCount; pageIndex++)
            {
                var page = document.BeginPage(600, 800);
                page.DrawBitmap(bitmap, new SKRect(0, 0, 600, 800));
                document.EndPage();
            }
            document.Close();
        }

        return stream.ToArray();
    }

    private static byte[] CreateMixedPdf()
    {
        using var stream = new MemoryStream();
        using var document = SKDocument.CreatePdf(stream);
        using var font = new SKFont(SKTypeface.Default, 28);
        using var paint = new SKPaint { Color = SKColors.Black, IsAntialias = true };

        var textPage = document.BeginPage(600, 800);
        textPage.DrawText(
            "This first page has useful selectable embedded text for PdfPig extraction.",
            40,
            100,
            font,
            paint);
        document.EndPage();

        using var bitmap = new SKBitmap(1_200, 1_600);
        using (var bitmapCanvas = new SKCanvas(bitmap))
        {
            bitmapCanvas.Clear(SKColors.White);
            bitmapCanvas.DrawText("SCANNED SECOND PAGE", 80, 180, font, paint);
        }

        var imagePage = document.BeginPage(600, 800);
        imagePage.DrawBitmap(bitmap, new SKRect(0, 0, 600, 800));
        document.EndPage();
        document.Close();
        return stream.ToArray();
    }

    private sealed class StubPdfOcrExtractor(PdfOcrResult result) : IPdfOcrExtractor
    {
        public IReadOnlyList<int> LastPageIndexes { get; private set; }

        public PdfOcrResult Extract(byte[] pdf, IReadOnlyList<int> pageIndexes)
        {
            LastPageIndexes = pageIndexes.ToList();
            return result;
        }
    }

    private sealed class ThrowingPdfOcrExtractor(Exception exception) : IPdfOcrExtractor
    {
        public PdfOcrResult Extract(byte[] pdf, IReadOnlyList<int> pageIndexes) =>
            throw exception;
    }
}
