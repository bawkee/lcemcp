namespace LceMcp;

internal static class OcrConfigValidator
{
    public static IReadOnlyList<string> Validate(OcrConfig config)
    {
        if (config is null || !config.Enabled)
            return [];

        var errors = new List<string>();
        var languages = config.Languages
            .Where(language => !string.IsNullOrWhiteSpace(language))
            .ToList();

        if (languages.Count > 4)
            errors.Add("ocr.languages accepts at most four language codes.");

        foreach (var language in languages)
        {
            if (!TesseractLanguagePackStore.IsValidLanguageCode(language))
                errors.Add($"ocr.languages contains invalid Tesseract code '{language}'.");
        }

        if (languages.Count == 0)
        {
            try
            {
                OcrScriptModels.Normalize(config.FallbackScript);
            }
            catch (AttachmentExtractionException)
            {
                errors.Add($"ocr.fallback_script '{config.FallbackScript}' is not a supported Tesseract script model.");
            }
        }

        return errors;
    }

    public static void ThrowIfInvalid(OcrConfig config)
    {
        var errors = Validate(config);
        if (errors.Count > 0)
            throw new CliException($"OCR config is invalid: {string.Join("; ", errors)}", 2);
    }
}
