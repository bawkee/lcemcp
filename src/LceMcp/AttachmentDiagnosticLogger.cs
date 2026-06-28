namespace LceMcp;

// Best-effort diagnostic logging for full extractor exceptions. User-facing state
// is committed independently, so logging failures must never escape this boundary.
internal sealed class AttachmentDiagnosticLogger
{
    private const long MaxLogBytes = 1_000_000;
    private const int RetainedFiles = 5;
    private static readonly object WriteLock = new();
    private readonly AppPaths _paths;
    private readonly TextWriter _error;

    public AttachmentDiagnosticLogger(AppPaths paths, TextWriter error = null)
    {
        _paths = paths;
        _error = error ?? Console.Error;
    }

    public void Write(
        int attachmentId,
        string stage,
        string triggerKind,
        string errorCode,
        string exceptionDetails)
    {
        if (string.IsNullOrWhiteSpace(exceptionDetails))
            return;

        var entry = $"""
            [{DateTimeOffset.UtcNow:O}] attachment_id={attachmentId} stage={stage} trigger={triggerKind} error_code={errorCode}
            {exceptionDetails}

            """;

        try
        {
            lock (WriteLock)
            {
                _paths.EnsureDataDirectories();
                var path = Path.Combine(_paths.LogsDirectory, "attachment-extraction.log");
                RotateIfNeeded(path, System.Text.Encoding.UTF8.GetByteCount(entry));
                File.AppendAllText(path, entry);
                _error.Write(entry);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            try
            {
                _error.WriteLine(
                    $"Attachment diagnostic logging failed for attachment_id={attachmentId} "
                    + $"stage={stage} error_code={errorCode}: {ex.Message}");
            }
            catch (Exception fallbackError) when (fallbackError is not OutOfMemoryException)
            {
                // Diagnostics must never roll back durable attachment state.
            }
        }
    }

    private static void RotateIfNeeded(string path, int incomingChars)
    {
        if (!File.Exists(path) || new FileInfo(path).Length + incomingChars <= MaxLogBytes)
            return;

        var oldest = $"{path}.{RetainedFiles}";
        if (File.Exists(oldest))
            File.Delete(oldest);

        for (var index = RetainedFiles - 1; index >= 1; index--)
        {
            var source = $"{path}.{index}";
            if (File.Exists(source))
                File.Move(source, $"{path}.{index + 1}");
        }

        File.Move(path, $"{path}.1");
    }
}
