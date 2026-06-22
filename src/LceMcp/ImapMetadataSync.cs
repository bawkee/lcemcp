using System.Text.Json;
using System.Text.RegularExpressions;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using MimeKit;

namespace LceMcp;

internal sealed class ImapMetadataSync
{
    private readonly EmailDatabase _database;

    public ImapMetadataSync(EmailDatabase database)
    {
        _database = database;
    }

    public async Task<MetadataAccountSyncResult> SyncAccountAsync(
        AccountConfig account,
        string password,
        int databaseAccountId,
        IReadOnlyList<StoredFolder> folders,
        int requestedSinceDays,
        IReadOnlyDictionary<int, int> effectiveSinceDaysByFolder,
        int maxPerFolder,
        int batchSize,
        CancellationToken cancellationToken,
        Action<int, int> progress = null,
        Action beforePersist = null)
    {
        using var client = new ImapClient();
        client.Timeout = 30_000;

        await client.ConnectAsync(
            account.ImapHost,
            account.ImapPort,
            ImapFolderDiscovery.ResolveSocketOptions(account.ImapSecurity),
            cancellationToken);

        client.AuthenticationMechanisms.Remove("XOAUTH2");
        await client.AuthenticateAsync(account.Username, password, cancellationToken);

        var capabilities = client.Capabilities.ToString();
        var discoveredFolders = await ImapFolderDiscovery.GetFoldersAsync(client, cancellationToken);
        var results = new List<MetadataFolderSyncResult>();

        var completedFolders = 0;
        foreach (var storedFolder in folders)
        {
            var effectiveSinceDays = effectiveSinceDaysByFolder is not null
                && effectiveSinceDaysByFolder.TryGetValue(storedFolder.Id, out var value)
                    ? value
                    : requestedSinceDays;

            results.Add(await SyncFolderAsync(
                client,
                discoveredFolders,
                databaseAccountId,
                storedFolder,
                requestedSinceDays,
                effectiveSinceDays,
                maxPerFolder,
                batchSize,
                cancellationToken,
                beforePersist));
            completedFolders++;
            progress?.Invoke(completedFolders, folders.Count);
        }

        await client.DisconnectAsync(true, cancellationToken);
        return new(account.Id, capabilities, results);
    }

    private async Task<MetadataFolderSyncResult> SyncFolderAsync(
        ImapClient client,
        IReadOnlyCollection<IMailFolder> discoveredFolders,
        int accountId,
        StoredFolder storedFolder,
        int requestedSinceDays,
        int effectiveSinceDays,
        int maxPerFolder,
        int batchSize,
        CancellationToken cancellationToken,
        Action beforePersist)
    {
        var fetchedCount = 0;
        var missingCount = 0;
        var persistedCount = 0;
        var matchedCount = 0;
        var selectedCount = 0;
        uint? highestUid = null;

        try
        {
            var folder = await ImapFolderDiscovery.ResolveFolderAsync(
                client,
                discoveredFolders,
                storedFolder.Path,
                cancellationToken);

            await folder.OpenAsync(FolderAccess.ReadOnly, cancellationToken);

            var searchQuery = BuildSearchQuery(effectiveSinceDays);
            var matchedUids = await folder.SearchAsync(searchQuery, cancellationToken);
            matchedCount = matchedUids.Count;
            var selectedUids = matchedUids
                .OrderByDescending(uid => uid.Id)
                .Take(maxPerFolder > 0 ? maxPerFolder : int.MaxValue)
                .ToList();
            selectedCount = selectedUids.Count;

            if (selectedCount == 0)
            {
                beforePersist?.Invoke();
                _database.MarkFolderSyncSucceeded(
                    accountId,
                    storedFolder.Id,
                    BuildStateJson(
                        requestedSinceDays,
                        effectiveSinceDays,
                        maxPerFolder,
                        matchedCount,
                        0,
                        0,
                        0,
                        null),
                    highestUid);

                return new(
                    storedFolder.Path,
                    matchedCount,
                    0,
                    0,
                    0,
                    0,
                    null,
                    Error: null,
                    RequestedSinceDays: requestedSinceDays,
                    EffectiveSinceDays: effectiveSinceDays,
                    AutoExpandedForGap: effectiveSinceDays > requestedSinceDays);
            }

            var fetchRequest = BuildFetchRequest(client.Capabilities);

            foreach (var batch in selectedUids.Chunk(batchSize))
            {
                var requested = batch.ToList();
                var summaries = await folder.FetchAsync(requested, fetchRequest, cancellationToken);
                var messages = summaries
                    .Where(summary => summary.UniqueId.IsValid)
                    .Select(ToMessageMetadata)
                    .ToList();

                fetchedCount += summaries.Count;
                missingCount += requested.Count - summaries.Count;

                var batchHighestUid = summaries.Count == 0
                    ? (uint?)null
                    : summaries.Max(summary => summary.UniqueId.Id);

                if (batchHighestUid is uint uid && (highestUid is null || uid > highestUid.Value))
                    highestUid = uid;

                beforePersist?.Invoke();
                persistedCount += _database.UpsertMessageMetadataBatch(
                    accountId,
                        storedFolder.Id,
                        messages,
                        BuildStateJson(
                            requestedSinceDays,
                            effectiveSinceDays,
                            maxPerFolder,
                            matchedCount,
                            selectedCount,
                        fetchedCount,
                        missingCount,
                        highestUid),
                    highestUid);
            }

            return new(
                storedFolder.Path,
                matchedCount,
                selectedCount,
                fetchedCount,
                missingCount,
                persistedCount,
                highestUid,
                Error: null,
                RequestedSinceDays: requestedSinceDays,
                EffectiveSinceDays: effectiveSinceDays,
                AutoExpandedForGap: effectiveSinceDays > requestedSinceDays);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            beforePersist?.Invoke();
            _database.MarkFolderSyncFailed(accountId, storedFolder.Id, ex.Message);
            return new(
                storedFolder.Path,
                matchedCount,
                selectedCount,
                fetchedCount,
                missingCount,
                persistedCount,
                highestUid,
                ex.Message,
                RequestedSinceDays: requestedSinceDays,
                EffectiveSinceDays: effectiveSinceDays,
                AutoExpandedForGap: effectiveSinceDays > requestedSinceDays);
        }
    }

    private static SearchQuery BuildSearchQuery(int sinceDays)
    {
        if (sinceDays <= 0)
            return SearchQuery.All;

        return SearchQuery.DeliveredAfter(DateTime.UtcNow.AddDays(-sinceDays));
    }

    private static FetchRequest BuildFetchRequest(ImapCapabilities capabilities)
    {
        var items = MessageSummaryItems.UniqueId
            | MessageSummaryItems.Envelope
            | MessageSummaryItems.Flags
            | MessageSummaryItems.InternalDate
            | MessageSummaryItems.Size
            | MessageSummaryItems.BodyStructure
            | MessageSummaryItems.References;

        if (capabilities.HasFlag(ImapCapabilities.ObjectID))
            items |= MessageSummaryItems.EmailId | MessageSummaryItems.ThreadId;

        if (capabilities.HasFlag(ImapCapabilities.GMailExt1))
            items |= MessageSummaryItems.GMailMessageId
                | MessageSummaryItems.GMailThreadId
                | MessageSummaryItems.GMailLabels;

        return new(items);
    }

    private static MessageMetadata ToMessageMetadata(IMessageSummary summary)
    {
        var envelope = summary.Envelope;
        var from = envelope?.From?.Mailboxes.FirstOrDefault();
        var providerMessageKey = ProviderMessageKey(summary);
        var providerThreadKey = ProviderThreadKey(summary);
        var messageIdHeader = CleanMessageId(envelope?.MessageId);
        var inReplyTo = CleanMessageId(envelope?.InReplyTo);
        var references = summary.References is { Count: > 0 }
            ? string.Join(" ", summary.References.Select(CleanMessageId).Where(value => !string.IsNullOrWhiteSpace(value)))
            : null;
        var normalizedSubject = NormalizeSubject(envelope?.Subject);

        return new(
            ProviderUid: summary.UniqueId.Id.ToString(),
            ProviderMessageKey: providerMessageKey,
            ProviderThreadKey: providerThreadKey,
            MessageIdHeader: messageIdHeader,
            InReplyTo: inReplyTo,
            ReferencesHeader: references,
            ThreadKey: BuildThreadKey(providerThreadKey, references, inReplyTo, messageIdHeader, normalizedSubject, from?.Address, envelope?.Date),
            Subject: BlankToNull(envelope?.Subject),
            NormalizedSubject: normalizedSubject,
            FromName: BlankToNull(from?.Name),
            FromEmail: BlankToNull(from?.Address),
            DateSent: FormatDate(envelope?.Date),
            DateReceived: FormatDate(summary.InternalDate),
            HasAttachments: summary.Attachments?.Any() == true,
            SizeBytes: summary.Size is null ? null : Convert.ToInt64(summary.Size.Value),
            RawHeaders: null,
            Flags: summary.Flags?.ToString(),
            Labels: summary.GMailLabels is { Count: > 0 } ? string.Join(",", summary.GMailLabels) : null);
    }

    private static string ProviderMessageKey(IMessageSummary summary)
    {
        if (!string.IsNullOrWhiteSpace(summary.EmailId))
            return $"emailid:{summary.EmailId.Trim()}";

        return summary.GMailMessageId is ulong gmailMessageId
            ? $"gmail:{gmailMessageId}"
            : null;
    }

    private static string ProviderThreadKey(IMessageSummary summary)
    {
        if (!string.IsNullOrWhiteSpace(summary.ThreadId))
            return $"threadid:{summary.ThreadId.Trim()}";

        return summary.GMailThreadId is ulong gmailThreadId
            ? $"gmail:{gmailThreadId}"
            : null;
    }

    private static string BuildThreadKey(
        string providerThreadKey,
        string references,
        string inReplyTo,
        string messageIdHeader,
        string normalizedSubject,
        string fromEmail,
        DateTimeOffset? dateSent)
    {
        if (!string.IsNullOrWhiteSpace(providerThreadKey))
            return providerThreadKey;

        var firstReference = references?
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();

        if (!string.IsNullOrWhiteSpace(firstReference))
            return $"message-id:{firstReference}";

        if (!string.IsNullOrWhiteSpace(inReplyTo))
            return $"message-id:{inReplyTo}";

        if (!string.IsNullOrWhiteSpace(messageIdHeader))
            return $"message-id:{messageIdHeader}";

        if (string.IsNullOrWhiteSpace(normalizedSubject) || string.IsNullOrWhiteSpace(fromEmail))
            return null;

        var datePart = dateSent?.UtcDateTime.ToString("yyyyMMdd") ?? "unknown-date";
        return $"fallback:{normalizedSubject}:{fromEmail.Trim().ToLowerInvariant()}:{datePart}";
    }

    private static string NormalizeSubject(string subject)
    {
        if (string.IsNullOrWhiteSpace(subject))
            return null;

        var normalized = SubjectPrefixRegex.Replace(subject.Trim(), "");
        normalized = WhitespaceRegex.Replace(normalized, " ").Trim();
        return string.IsNullOrWhiteSpace(normalized)
            ? null
            : normalized.ToLowerInvariant();
    }

    private static string CleanMessageId(string messageId)
    {
        if (string.IsNullOrWhiteSpace(messageId))
            return null;

        return messageId.Trim().Trim('<', '>');
    }

    private static string FormatDate(DateTimeOffset? value)
    {
        return value?.ToUniversalTime().ToString("O");
    }

    private static string BlankToNull(string value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    private static string BuildStateJson(
        int requestedSinceDays,
        int effectiveSinceDays,
        int maxPerFolder,
        int matchedCount,
        int selectedCount,
        int fetchedCount,
        int missingCount,
        uint? highestUid)
    {
        return JsonSerializer.Serialize(new
        {
            since_days = effectiveSinceDays,
            requested_since_days = requestedSinceDays,
            effective_since_days = effectiveSinceDays,
            auto_expanded_for_gap = effectiveSinceDays > requestedSinceDays,
            max_per_folder = maxPerFolder,
            matched_count = matchedCount,
            selected_count = selectedCount,
            fetched_count = fetchedCount,
            missing_count = missingCount,
            highest_uid = highestUid,
            updated_at = DateTimeOffset.UtcNow.ToString("O")
        });
    }

    private static readonly Regex SubjectPrefixRegex = new(
        @"^\s*((re|fw|fwd)\s*:\s*)+",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex WhitespaceRegex = new(
        @"\s+",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
}
