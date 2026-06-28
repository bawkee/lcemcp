namespace LceMcp;

// Converts implementation exceptions into stable domain codes used by persisted
// retry policy and MCP responses. Runtime exception types remain diagnostic detail.
internal static class AttachmentFailureClassifier
{
    public static AttachmentFailureClassification Classify(Exception exception)
    {
        if (exception.GetType().Name.Contains("Encrypted", StringComparison.OrdinalIgnoreCase)
            || exception is System.Security.Cryptography.CryptographicException)
            return new("encrypted_document", "The attachment is encrypted and cannot be extracted.");

        if (exception is FormatException
            || exception.GetType().Namespace?.StartsWith("DocumentFormat.OpenXml", StringComparison.Ordinal) == true
            || exception.GetType().Namespace?.StartsWith("UglyToad.PdfPig", StringComparison.Ordinal) == true)
            return new("invalid_document", "The attachment is invalid or corrupt.");

        return exception switch
        {
            AttachmentSizeLimitException => new("attachment_too_large", "The attachment exceeded the configured download limit."),
            TimeoutException => new("extractor_timeout", "Attachment extraction timed out."),
            IOException => new("temporary_io_failure", "A temporary I/O error interrupted attachment processing."),
            UnauthorizedAccessException => new("temporary_io_failure", "Attachment storage was temporarily unavailable."),
            InvalidDataException => new("invalid_document", "The attachment is invalid or corrupt."),
            _ => new("unknown_extractor_failure", "The attachment extractor failed unexpectedly.")
        };
    }

    public static bool IsTransient(string errorCode) =>
        errorCode is "extractor_timeout"
            or "extractor_unavailable"
            or "temporary_io_failure"
            or "worker_canceled"
            or "worker_crashed";

    public static bool IsUnknown(string errorCode) =>
        errorCode == "unknown_extractor_failure";

    public static int AutomaticAttemptBudget(string errorCode) =>
        IsTransient(errorCode) ? 3 : IsUnknown(errorCode) ? 2 : 1;
}

internal sealed record AttachmentFailureClassification(
    string ErrorCode,
    string Summary);
