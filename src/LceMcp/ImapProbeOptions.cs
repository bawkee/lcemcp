namespace LceMcp;

internal sealed class ImapProbeOptions
{
    public string Folder { get; init; } = "INBOX";
    public string Query { get; init; }
    public int SinceDays { get; init; } = 30;
    public int Limit { get; init; } = 5;
    public bool FetchFirstBody { get; init; }
    public int BodyChars { get; init; } = 1200;
}
