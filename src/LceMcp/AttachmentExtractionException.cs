namespace LceMcp;

internal sealed class AttachmentExtractionException : Exception
{
    public AttachmentExtractionException(string errorCode, string safeSummary, Exception innerException = null)
        : base(safeSummary, innerException)
    {
        ErrorCode = errorCode;
        SafeSummary = safeSummary;
    }

    public string ErrorCode { get; }
    public string SafeSummary { get; }
}
