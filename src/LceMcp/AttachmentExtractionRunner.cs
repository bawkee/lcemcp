namespace LceMcp;

// Claims failed extraction work and reruns it from the managed local object store.
// This path never contacts IMAP or changes the attachment's stable local ID.
internal sealed class AttachmentExtractionRunner
{
    private readonly EmailDatabase _database;
    private readonly AttachmentObjectStore _objectStore;
    private readonly AttachmentProcessor _processor;

    public AttachmentExtractionRunner(
        EmailDatabase database,
        Func<AttachmentExtractionInput, AttachmentExtractionOutput> terminalExtractor = null,
        OcrConfig ocrConfig = null)
    {
        _database = database;
        _objectStore = new(database.Paths);
        _processor = new(_objectStore, terminalExtractor, ocrConfig: ocrConfig);
    }

    public AttachmentRetryResult RetryExplicit(
        AttachmentExtractionFailureQuery request,
        string clientName = null,
        CancellationToken cancellationToken = default)
    {
        var failures = _database.ListAttachmentExtractionFailures(request with
        {
            Status = "open",
            Limit = Math.Min(request.Limit, 50)
        });
        var ids = failures
            .Where(failure => failure.Stage == "extraction")
            .Select(failure => failure.AttachmentId)
            .Distinct()
            .Take(Math.Min(request.Limit, 50))
            .ToList();
        return Process(ids, "explicit_retry", clientName, cancellationToken);
    }

    public AttachmentRetryResult ProcessDue(
        string accountFilter,
        int limit = 50,
        CancellationToken cancellationToken = default,
        DateTimeOffset? now = null)
    {
        var ids = _database.ReadDueAttachmentExtractionIds(accountFilter, limit, now);
        return Process(ids, "automatic_retry", clientName: null, cancellationToken, now);
    }

    private AttachmentRetryResult Process(
        IReadOnlyList<int> attachmentIds,
        string triggerKind,
        string clientName,
        CancellationToken cancellationToken,
        DateTimeOffset? now = null)
    {
        var results = new List<AttachmentRetryItemResult>();

        foreach (var attachmentId in attachmentIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var itemTriggerKind = triggerKind == "automatic_retry"
                && _database.IsPdfOcrUpgradeCandidate(attachmentId)
                ? "extractor_upgrade"
                : triggerKind;
            var claim = _database.ClaimAttachmentExtraction(attachmentId, itemTriggerKind, clientName, now);
            if (claim is null)
            {
                results.Add(new(attachmentId, "skipped", null, "Attachment is not claimable."));
                continue;
            }

            AttachmentContent processed;
            try
            {
                var content = _objectStore.Read(claim.Attachment.StorageKey);
                processed = _processor.ProcessStoredAttachment(claim.Attachment, content);
            }
            catch (OperationCanceledException)
            {
                var canceled = FailureResult(
                    claim.Attachment,
                    new("worker_canceled", "Attachment extraction was canceled."),
                    new OperationCanceledException("Attachment extraction was canceled."));
                _database.CompleteAttachmentExtraction(claim, canceled, now);
                throw;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                var classified = AttachmentFailureClassifier.Classify(ex);
                processed = FailureResult(claim.Attachment, classified, ex);
            }

            if (!_database.CompleteAttachmentExtraction(claim, processed, now))
            {
                results.Add(new(attachmentId, "skipped", null, "Attachment extraction lease was lost."));
                continue;
            }

            results.Add(new(
                attachmentId,
                processed.ExtractionStatus,
                processed.ExtractionErrorCode,
                processed.ExtractionError));
        }

        return new(results);
    }

    private static AttachmentContent FailureResult(
        StoredAttachment attachment,
        AttachmentFailureClassification failure,
        Exception exception) =>
        new(
            attachment.SourceKind,
            attachment.PartId,
            attachment.Filename,
            attachment.DisplayPath,
            attachment.ArchiveEntryPath,
            attachment.MimeType,
            attachment.SniffedMimeType,
            attachment.SizeBytes,
            attachment.CompressedSizeBytes,
            attachment.UncompressedSizeBytes,
            attachment.ContentHash,
            attachment.StorageKey,
            attachment.IsContainer,
            attachment.NestingDepth,
            attachment.DownloadStatus,
            attachment.DownloadError,
            "failed",
            failure.Summary,
            null,
            null,
            attachment.Extractor,
            [],
            failure.ErrorCode,
            attachment.ExtractorVersion,
            exception.GetType().FullName,
            exception.Message,
            exception.ToString());
}
