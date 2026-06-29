using System.Net;

namespace LceMcp;

// Language data is executable model input. Downloads therefore use one immutable
// commit from Tesseract's official tessdata_fast repository, never a caller URL.
internal sealed partial class TesseractLanguagePackStore
{
    internal const string TessdataCommit = "87416418657359cb625c412a48b6e1d6d41c29bd";
    internal const long MaxLanguagePackBytes = 100L * 1024 * 1024;
    internal const string RepositoryUrl = "https://github.com/tesseract-ocr/tessdata_fast";
    private const string RepositoryBase =
        "https://raw.githubusercontent.com/tesseract-ocr/tessdata_fast/" + TessdataCommit;
    private static readonly string[] SupportedLanguages =
    [
        "afr", "amh", "ara", "asm", "aze", "aze_cyrl", "bel", "ben", "bod", "bos", "bre", "bul",
        "cat", "ceb", "ces", "chi_sim", "chi_sim_vert", "chi_tra", "chi_tra_vert", "chr", "cos",
        "cym", "dan", "deu", "deu_latf", "div", "dzo", "ell", "eng", "enm", "epo", "equ", "est",
        "eus", "fao", "fas", "fil", "fin", "fra", "frk", "frm", "fry", "gla", "gle", "glg",
        "grc", "guj", "hat", "heb", "hin", "hrv", "hun", "hye", "iku", "ind", "isl", "ita",
        "ita_old", "jav", "jpn", "jpn_vert", "kan", "kat", "kat_old", "kaz", "khm", "kir", "kmr",
        "kor", "kor_vert", "lao", "lat", "lav", "lit", "ltz", "mal", "mar", "mkd", "mlt", "mon",
        "mri", "msa", "mya", "nep", "nld", "nor", "oci", "ori", "pan", "pol", "por", "pus",
        "que", "ron", "rus", "san", "sin", "slk", "slv", "snd", "spa", "spa_old", "sqi", "srp",
        "srp_latn", "sun", "swa", "swe", "syr", "tam", "tat", "tel", "tgk", "tha", "tir", "ton",
        "tur", "uig", "ukr", "urd", "uzb", "uzb_cyrl", "vie", "yid", "yor"
    ];
    private static readonly HashSet<string> SupportedLanguageSet = new(
        SupportedLanguages,
        StringComparer.OrdinalIgnoreCase);
    private static readonly HttpClient SharedHttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(90)
    };
    private static readonly SemaphoreSlim DownloadGate = new(1, 1);

    private readonly AppPaths _paths;
    private readonly bool _allowDownloads;
    private readonly HttpClient _httpClient;

    public TesseractLanguagePackStore(
        AppPaths paths,
        bool allowDownloads,
        HttpClient httpClient = null)
    {
        _paths = paths;
        _allowDownloads = allowDownloads;
        _httpClient = httpClient ?? SharedHttpClient;
    }

    public string EnsureOrientationModel() =>
        Ensure("osd", "osd.traineddata");

    public string EnsureLanguage(string language)
    {
        language = NormalizeLanguageCode(language);
        return Ensure(language, $"{language}.traineddata");
    }

    public string EnsureScript(string script)
    {
        script = OcrScriptModels.Normalize(script);
        return Ensure(script, $"script/{script}.traineddata");
    }

    public IReadOnlyList<string> ListCachedModels()
    {
        if (!Directory.Exists(_paths.TessdataDirectory))
            return [];

        return Directory
            .EnumerateFiles(_paths.TessdataDirectory, "*.traineddata", SearchOption.TopDirectoryOnly)
            .Where(IsUsableModel)
            .Select(Path.GetFileNameWithoutExtension)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    internal Uri BuildOfficialUri(string repositoryPath) =>
        new($"{RepositoryBase}/{repositoryPath}");

    private string Ensure(string modelName, string repositoryPath)
    {
        var destination = Path.Combine(_paths.TessdataDirectory, $"{modelName}.traineddata");
        if (IsUsableModel(destination))
            return modelName;

        if (!_allowDownloads)
        {
            throw new AttachmentExtractionException(
                "ocr_language_pack_missing",
                $"OCR model '{modelName}' is not cached and automatic model downloads are disabled.");
        }

        DownloadGate.Wait();
        try
        {
            if (IsUsableModel(destination))
                return modelName;

            Directory.CreateDirectory(_paths.TessdataDirectory);
            Download(BuildOfficialUri(repositoryPath), destination);
            return modelName;
        }
        finally
        {
            DownloadGate.Release();
        }
    }

    private void Download(Uri source, string destination)
    {
        var temporaryPath = Path.Combine(
            _paths.TessdataDirectory,
            $".{Path.GetFileName(destination)}.{Guid.NewGuid():N}.tmp");

        try
        {
            using var response = _httpClient
                .GetAsync(source, HttpCompletionOption.ResponseHeadersRead)
                .GetAwaiter()
                .GetResult();

            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                throw new AttachmentExtractionException(
                    "ocr_language_pack_missing",
                    $"The requested OCR model is not present in the pinned official tessdata_fast revision.");
            }

            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > MaxLanguagePackBytes)
            {
                throw new AttachmentExtractionException(
                    "ocr_safety_limit",
                    "The OCR model exceeded the configured download limit.");
            }

            using var sourceStream = response.Content.ReadAsStream();
            using var destinationStream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 64 * 1024,
                FileOptions.WriteThrough);
            var buffer = new byte[64 * 1024];
            long total = 0;

            while (true)
            {
                var read = sourceStream.Read(buffer, 0, buffer.Length);
                if (read == 0)
                    break;

                total += read;
                if (total > MaxLanguagePackBytes)
                {
                    throw new AttachmentExtractionException(
                        "ocr_safety_limit",
                        "The OCR model exceeded the configured download limit.");
                }

                destinationStream.Write(buffer, 0, read);
            }

            destinationStream.Flush(flushToDisk: true);
            destinationStream.Close();
            if (total < 1_024)
            {
                throw new AttachmentExtractionException(
                    "ocr_language_pack_missing",
                    "The downloaded OCR model was unexpectedly small.");
            }

            File.Move(temporaryPath, destination, overwrite: false);
        }
        catch (AttachmentExtractionException)
        {
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException)
        {
            throw new AttachmentExtractionException(
                "temporary_io_failure",
                "The OCR model could not be downloaded from the official Tesseract repository.",
                ex);
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    public static IReadOnlyList<string> ListSupportedLanguages() =>
        SupportedLanguages;

    internal static string NormalizeLanguageCode(string language)
    {
        language = language?.Trim().ToLowerInvariant();
        if (!IsValidLanguageCode(language))
        {
            throw new AttachmentExtractionException(
                "ocr_language_pack_missing",
                "The configured Tesseract language code is not present in the pinned official tessdata_fast revision.");
        }

        return language;
    }

    internal static bool IsValidLanguageCode(string language) =>
        !string.IsNullOrWhiteSpace(language)
        && SupportedLanguageSet.Contains(language.Trim().ToLowerInvariant());

    private static bool IsUsableModel(string path) =>
        File.Exists(path) && new FileInfo(path).Length >= 1_024;
}

internal static class OcrScriptModels
{
    private static readonly IReadOnlyDictionary<string, string> Names =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Arabic"] = "Arabic",
            ["Armenian"] = "Armenian",
            ["Bengali"] = "Bengali",
            ["CanadianAboriginal"] = "Canadian_Aboriginal",
            ["Canadian_Aboriginal"] = "Canadian_Aboriginal",
            ["Cherokee"] = "Cherokee",
            ["Cyrillic"] = "Cyrillic",
            ["Devanagari"] = "Devanagari",
            ["Ethiopic"] = "Ethiopic",
            ["Fraktur"] = "Fraktur",
            ["Georgian"] = "Georgian",
            ["Greek"] = "Greek",
            ["Gujarati"] = "Gujarati",
            ["Gurmukhi"] = "Gurmukhi",
            ["HanS"] = "HanS",
            ["HanSVert"] = "HanS_vert",
            ["HanS_vert"] = "HanS_vert",
            ["HanT"] = "HanT",
            ["HanTVert"] = "HanT_vert",
            ["HanT_vert"] = "HanT_vert",
            ["Hangul"] = "Hangul",
            ["HangulVert"] = "Hangul_vert",
            ["Hangul_vert"] = "Hangul_vert",
            ["Hebrew"] = "Hebrew",
            ["Japanese"] = "Japanese",
            ["JapaneseVert"] = "Japanese_vert",
            ["Japanese_vert"] = "Japanese_vert",
            ["Kannada"] = "Kannada",
            ["Khmer"] = "Khmer",
            ["Lao"] = "Lao",
            ["Latin"] = "Latin",
            ["Malayalam"] = "Malayalam",
            ["Myanmar"] = "Myanmar",
            ["Oriya"] = "Oriya",
            ["Sinhala"] = "Sinhala",
            ["Syriac"] = "Syriac",
            ["Tamil"] = "Tamil",
            ["Telugu"] = "Telugu",
            ["Thaana"] = "Thaana",
            ["Thai"] = "Thai",
            ["Tibetan"] = "Tibetan",
            ["Vietnamese"] = "Vietnamese"
        };

    public static string Normalize(string value)
    {
        value = value?.Trim();
        if (value is not null && Names.TryGetValue(value, out var normalized))
            return normalized;

        throw new AttachmentExtractionException(
            "ocr_language_pack_missing",
            $"Tesseract does not provide a supported script model named '{value ?? ""}'.");
    }

    public static IReadOnlyList<string> ListSupported() =>
        Names.Values
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
}
