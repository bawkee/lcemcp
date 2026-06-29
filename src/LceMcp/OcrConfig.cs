namespace LceMcp;

internal sealed class OcrConfig
{
    public bool Enabled { get; set; }
    public bool AutoDownloadLanguagePacks { get; set; } = true;
    public string FallbackScript { get; set; } = "Latin";
    public List<string> Languages { get; } = [];
}
