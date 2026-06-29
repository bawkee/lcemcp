using PDFtoImage;
using SkiaSharp;
using System.Text;
using TesseractOCR;
using TesseractOCR.Enums;
using PixImage = TesseractOCR.Pix.Image;

namespace LceMcp;

internal interface IPdfOcrExtractor
{
    PdfOcrResult Extract(byte[] pdf, IReadOnlyList<int> pageIndexes);
}

internal sealed record PdfOcrResult(
    string Text,
    string Model,
    string ExtractorVersion);

internal sealed class PdfOcrExtractor : IPdfOcrExtractor
{
    internal const int RenderDpi = 200;
    internal const int MaxOcrPages = 20;
    internal const int MaxRenderedDimension = 6_000;
    internal const long MaxRenderedPixels = 20_000_000;
    internal const int MaxEncodedPageBytes = 25 * 1024 * 1024;
    internal const string ExtractorVersion =
        "TesseractOCR 5.5.2; PDFtoImage 5.2.1; tessdata_fast@" + TesseractLanguagePackStore.TessdataCommit;

    private readonly AppPaths _paths;
    private readonly OcrConfig _config;
    private readonly TesseractLanguagePackStore _languagePacks;

    public PdfOcrExtractor(
        AppPaths paths,
        OcrConfig config,
        TesseractLanguagePackStore languagePacks = null)
    {
        _paths = paths;
        _config = config;
        _languagePacks = languagePacks
            ?? new(paths, config.AutoDownloadLanguagePacks);
    }

    public PdfOcrResult Extract(byte[] pdf, IReadOnlyList<int> pageIndexes)
    {
        if (pageIndexes.Count == 0)
            return new(null, null, ExtractorVersion);

        if (pageIndexes.Count > MaxOcrPages)
        {
            throw new AttachmentExtractionException(
                "ocr_safety_limit",
                $"PDF OCR is limited to {MaxOcrPages} candidate pages per attachment.");
        }

#pragma warning disable CA1416 // PDFtoImage supports every desktop runtime targeted by this application.
        var pageSizes = Conversion.GetPageSizes(pdf);
#pragma warning restore CA1416
        if (pageIndexes.Any(index => index < 0 || index >= pageSizes.Count))
            throw new AttachmentExtractionException("invalid_document", "The PDF page index is invalid.");

        var configuredLanguages = _config.Languages
            .Where(language => !string.IsNullOrWhiteSpace(language))
            .Select(language => language.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (configuredLanguages.Count > 4)
        {
            throw new AttachmentExtractionException(
                "ocr_safety_limit",
                "PDF OCR accepts at most four configured languages.");
        }

        var firstPage = RenderPage(pdf, pageIndexes[0], pageSizes[pageIndexes[0]]);
        string model;
        var detectOrientation = configuredLanguages.Count == 0;

        if (detectOrientation)
        {
            _languagePacks.EnsureOrientationModel();
            var detectedScript = DetectScript(firstPage, _config.FallbackScript);
            model = _languagePacks.EnsureScript(detectedScript.Script);
            firstPage = Rotate(firstPage, detectedScript.Orientation);
        }
        else
        {
            foreach (var language in configuredLanguages)
                _languagePacks.EnsureLanguage(language);

            model = string.Join("+", configuredLanguages.Select(language => language.ToLowerInvariant()));
        }

        using var engine = CreateEngine(model);
        var text = new StringBuilder();
        AppendPageText(text, engine, firstPage);

        foreach (var pageIndex in pageIndexes.Skip(1))
        {
            var rendered = RenderPage(pdf, pageIndex, pageSizes[pageIndex]);
            if (detectOrientation)
            {
                var detectedScript = DetectScript(rendered, _config.FallbackScript);
                rendered = Rotate(rendered, detectedScript.Orientation);
            }

            AppendPageText(text, engine, rendered);
        }

        return new(text.ToString(), model, ExtractorVersion);
    }

    private Engine CreateEngine(string model)
    {
        try
        {
            return new Engine(_paths.TessdataDirectory, model, EngineMode.LstmOnly);
        }
        catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException or TypeInitializationException)
        {
            throw new AttachmentExtractionException(
                "extractor_unavailable",
                "The local Tesseract OCR engine is unavailable.",
                ex);
        }
    }

    private DetectedScript DetectScript(byte[] imageBytes, string fallbackScript)
    {
        fallbackScript = OcrScriptModels.Normalize(fallbackScript);

        try
        {
            using var engine = new Engine(_paths.TessdataDirectory, "osd", EngineMode.Default);
            using var image = PixImage.LoadFromMemory(imageBytes);
            using var page = engine.Process(image, PageSegMode.OsdOnly);
            page.DetectOrientationAndScript(
                out var orientation,
                out _,
                out var script,
                out var scriptConfidence);

            var detected = script == ScriptName.Unknown || scriptConfidence <= 0
                ? fallbackScript
                : OcrScriptModels.Normalize(script.ToString());
            return new(detected, orientation);
        }
        catch (AttachmentExtractionException)
        {
            throw;
        }
        catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException or TypeInitializationException)
        {
            throw new AttachmentExtractionException(
                "extractor_unavailable",
                "The local Tesseract OCR engine is unavailable.",
                ex);
        }
        catch
        {
            // Sparse scans often do not have enough characters for OSD. PDF page
            // rotation is normally respected by PDFium, so zero is the safe fallback.
            return new(fallbackScript, 0);
        }
    }

    private static byte[] RenderPage(byte[] pdf, int pageIndex, System.Drawing.SizeF pageSize)
    {
        var width = (long)Math.Ceiling(pageSize.Width * RenderDpi / 72d);
        var height = (long)Math.Ceiling(pageSize.Height * RenderDpi / 72d);
        if (width <= 0
            || height <= 0
            || width > MaxRenderedDimension
            || height > MaxRenderedDimension
            || width * height > MaxRenderedPixels)
        {
            throw new AttachmentExtractionException(
                "ocr_safety_limit",
                "A PDF page exceeded the OCR rasterization dimensions.");
        }

        try
        {
#pragma warning disable CA1416 // PDFtoImage supports every desktop runtime targeted by this application.
            using var bitmap = Conversion.ToImage(
                pdf,
                pageIndex,
                options: new(Dpi: RenderDpi, Grayscale: true));
#pragma warning restore CA1416
            if (bitmap.Width > MaxRenderedDimension
                || bitmap.Height > MaxRenderedDimension
                || (long)bitmap.Width * bitmap.Height > MaxRenderedPixels)
            {
                throw new AttachmentExtractionException(
                    "ocr_safety_limit",
                    "A rendered PDF page exceeded the OCR pixel limit.");
            }

            using var encoded = bitmap.Encode(SKEncodedImageFormat.Png, 90);
            if (encoded is null || encoded.Size > MaxEncodedPageBytes)
            {
                throw new AttachmentExtractionException(
                    "ocr_safety_limit",
                    "A rendered PDF page exceeded the OCR image limit.");
            }

            return encoded.ToArray();
        }
        catch (AttachmentExtractionException)
        {
            throw;
        }
        catch (PDFtoImage.Exceptions.PdfPasswordProtectedException ex)
        {
            throw new AttachmentExtractionException(
                "encrypted_document",
                "The attachment is encrypted and cannot be rendered for OCR.",
                ex);
        }
        catch (PDFtoImage.Exceptions.PdfException ex)
        {
            throw new AttachmentExtractionException(
                "invalid_document",
                "The PDF could not be rendered for OCR.",
                ex);
        }
        catch (Exception ex) when (ex is DllNotFoundException or BadImageFormatException or TypeInitializationException)
        {
            throw new AttachmentExtractionException(
                "extractor_unavailable",
                "The local PDF renderer is unavailable.",
                ex);
        }
    }

    private static byte[] Rotate(byte[] imageBytes, int orientation)
    {
        orientation = ((orientation % 360) + 360) % 360;
        if (orientation == 0)
            return imageBytes;

        using var source = SKBitmap.Decode(imageBytes);
        if (source is null)
            throw new AttachmentExtractionException("invalid_document", "The rendered OCR image is invalid.");

        var swapDimensions = orientation is 90 or 270;
        using var rotated = new SKBitmap(
            swapDimensions ? source.Height : source.Width,
            swapDimensions ? source.Width : source.Height);
        using (var canvas = new SKCanvas(rotated))
        {
            canvas.Clear(SKColors.White);
            canvas.Translate(rotated.Width / 2f, rotated.Height / 2f);
            canvas.RotateDegrees(orientation);
            canvas.Translate(-source.Width / 2f, -source.Height / 2f);
            canvas.DrawBitmap(source, 0, 0);
        }

        using var encoded = rotated.Encode(SKEncodedImageFormat.Png, 90);
        return encoded.ToArray();
    }

    private static void AppendPageText(StringBuilder builder, Engine engine, byte[] imageBytes)
    {
        using var image = PixImage.LoadFromMemory(imageBytes);
        using var page = engine.Process(image, PageSegMode.Auto);
        if (string.IsNullOrWhiteSpace(page.Text))
            return;

        if (builder.Length > 0)
            builder.AppendLine();
        builder.Append(page.Text);
    }

    private sealed record DetectedScript(string Script, int Orientation);
}
