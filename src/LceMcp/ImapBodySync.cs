using MailKit;
using MailKit.Net.Imap;
using MimeKit;

namespace LceMcp;

internal sealed class ImapBodySync
{
    private const long MaxBatchedFullMessageBytes = 512 * 1024;
    private readonly EmailDatabase _database;

    public ImapBodySync(EmailDatabase database)
    {
        _database = database;
    }

    public async Task<BodyAccountSyncResult> SyncAccountAsync(
        AccountConfig account,
        string password,
        string folderFilter,
        int maxPerFolder,
        int batchSize,
        CancellationToken cancellationToken,
        Action<int, int> progress = null,
        Action beforePersist = null)
    {
        var targets = _database.ReadPendingBodySyncTargets(account.Id, folderFilter, maxPerFolder);
        if (targets.Count == 0)
            return new(account.Id, []);

        using var client = new ImapClient();
        client.Timeout = 30_000;

        await client.ConnectAsync(
            account.ImapHost,
            account.ImapPort,
            ImapFolderDiscovery.ResolveSocketOptions(account.ImapSecurity),
            cancellationToken);

        client.AuthenticationMechanisms.Remove("XOAUTH2");
        await client.AuthenticateAsync(account.Username, password, cancellationToken);

        var discoveredFolders = await ImapFolderDiscovery.GetFoldersAsync(client, cancellationToken);
        var results = new List<BodyFolderSyncResult>();
        var completedTargets = 0;

        foreach (var group in targets.GroupBy(target => new { target.FolderId, target.FolderPath }))
        {
            var folderTargets = group.ToList();
            results.Add(await SyncFolderAsync(
                client,
                discoveredFolders,
                group.Key.FolderPath,
                folderTargets,
                batchSize,
                cancellationToken,
                beforePersist,
                () =>
                {
                    completedTargets++;
                    progress?.Invoke(completedTargets, targets.Count);
                }));
        }

        await client.DisconnectAsync(true, cancellationToken);
        return new(account.Id, results);
    }

    private async Task<BodyFolderSyncResult> SyncFolderAsync(
        ImapClient client,
        IReadOnlyCollection<IMailFolder> discoveredFolders,
        string folderPath,
        IReadOnlyList<BodySyncTarget> targets,
        int batchSize,
        CancellationToken cancellationToken,
        Action beforePersist,
        Action progress)
    {
        var fetchedCount = 0;
        var persistedCount = 0;
        var missingCount = 0;

        try
        {
            var folder = await ImapFolderDiscovery.ResolveFolderAsync(
                client,
                discoveredFolders,
                folderPath,
                cancellationToken);

            await folder.OpenAsync(FolderAccess.ReadOnly, cancellationToken);

            foreach (var batch in targets.Chunk(batchSize))
            {
                var requested = new List<(BodySyncTarget Target, UniqueId Uid)>();

                foreach (var target in batch)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (!uint.TryParse(target.ProviderUid, out var uidValue))
                    {
                        missingCount++;
                        progress?.Invoke();
                        continue;
                    }

                    requested.Add((target, new(uidValue)));
                }

                if (requested.Count == 0)
                    continue;

                var fullMessageItems = requested
                    .Where(item => ShouldBatchFullMessage(item.Target))
                    .ToList();
                var textPartItems = requested
                    .Where(item => !ShouldBatchFullMessage(item.Target))
                    .ToList();
                var bodies = new List<MessageBodyContent>();
                var completedInBatch = requested.Count;

                var fullMessageResult = await FetchFullMessageBatchAsync(folder, fullMessageItems, cancellationToken);
                bodies.AddRange(fullMessageResult.Bodies);
                fetchedCount += fullMessageResult.FetchedCount;
                missingCount += fullMessageResult.MissingCount;

                var textPartResult = await FetchTextPartBatchAsync(folder, textPartItems, cancellationToken);
                bodies.AddRange(textPartResult.Bodies);
                fetchedCount += textPartResult.FetchedCount;
                missingCount += textPartResult.MissingCount;

                beforePersist?.Invoke();
                _database.UpsertMessageBodies(bodies);
                persistedCount += bodies.Count;

                for (var i = 0; i < completedInBatch; i++)
                    progress?.Invoke();
            }

            return new(folderPath, targets.Count, fetchedCount, persistedCount, missingCount, Error: null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new(folderPath, targets.Count, fetchedCount, persistedCount, missingCount, ex.Message);
        }
    }

    private static bool ShouldBatchFullMessage(BodySyncTarget target) =>
        !target.HasAttachments
        && target.SizeBytes is long sizeBytes
        && sizeBytes <= MaxBatchedFullMessageBytes;

    private static async Task<BodyBatchFetchResult> FetchFullMessageBatchAsync(
        IMailFolder folder,
        IReadOnlyList<(BodySyncTarget Target, UniqueId Uid)> items,
        CancellationToken cancellationToken)
    {
        if (items.Count == 0)
            return EmptyBatchFetchResult;

        var bodies = new List<MessageBodyContent>();
        var fetchedUids = new HashSet<uint>();
        var targetsByUid = items.ToDictionary(item => item.Uid.Id, item => item.Target);

        if (folder is not IImapFolder imapFolder)
            return await FetchFullMessagesIndividuallyAsync(folder, items, cancellationToken);

        try
        {
            await imapFolder.GetStreamsAsync(
                items.Select(item => item.Uid).ToList(),
                async (_, _, uid, stream, token) =>
                {
                    if (!targetsByUid.TryGetValue(uid.Id, out var target))
                        return;

                    var message = await MimeMessage.LoadAsync(stream, token);
                    bodies.Add(MessageBodyNormalizer.FromMimeMessage(target.MessageId, message));
                    fetchedUids.Add(uid.Id);
                },
                cancellationToken);
        }
        catch (MessageNotFoundException)
        {
            return await FetchFullMessagesIndividuallyAsync(folder, items, cancellationToken);
        }

        return new(
            Bodies: bodies,
            FetchedCount: bodies.Count,
            MissingCount: items.Count - fetchedUids.Count);
    }

    private static async Task<BodyBatchFetchResult> FetchFullMessagesIndividuallyAsync(
        IMailFolder folder,
        IReadOnlyList<(BodySyncTarget Target, UniqueId Uid)> items,
        CancellationToken cancellationToken)
    {
        var bodies = new List<MessageBodyContent>();
        var missingCount = 0;

        foreach (var item in items)
        {
            try
            {
                var message = await folder.GetMessageAsync(item.Uid, cancellationToken);
                bodies.Add(MessageBodyNormalizer.FromMimeMessage(item.Target.MessageId, message));
            }
            catch (MessageNotFoundException)
            {
                missingCount++;
            }
        }

        return new(
            Bodies: bodies,
            FetchedCount: bodies.Count,
            MissingCount: missingCount);
    }

    private static async Task<BodyBatchFetchResult> FetchTextPartBatchAsync(
        IMailFolder folder,
        IReadOnlyList<(BodySyncTarget Target, UniqueId Uid)> items,
        CancellationToken cancellationToken)
    {
        if (items.Count == 0)
            return EmptyBatchFetchResult;

        var summaries = await folder.FetchAsync(
            items.Select(item => item.Uid).ToList(),
            BodyFetchRequest,
            cancellationToken);
        var summariesByUid = summaries
            .Where(summary => summary.UniqueId.IsValid)
            .ToDictionary(summary => summary.UniqueId.Id);
        var bodies = new List<MessageBodyContent>();
        var missingCount = 0;

        foreach (var item in items)
        {
            try
            {
                if (!summariesByUid.TryGetValue(item.Uid.Id, out var summary))
                {
                    missingCount++;
                    continue;
                }

                bodies.Add(await FetchBodyContentAsync(folder, item.Uid, item.Target.MessageId, summary, cancellationToken));
            }
            catch (MessageNotFoundException)
            {
                missingCount++;
            }
        }

        return new(
            Bodies: bodies,
            FetchedCount: bodies.Count,
            MissingCount: missingCount);
    }

    private static async Task<MessageBodyContent> FetchBodyContentAsync(
        IMailFolder folder,
        UniqueId uid,
        int messageId,
        IMessageSummary summary,
        CancellationToken cancellationToken)
    {
        var recipients = ReadRecipients(summary.Envelope);
        var plainText = await FetchTextPartAsync(folder, uid, summary.TextBody, cancellationToken);

        if (!string.IsNullOrWhiteSpace(plainText))
            return MessageBodyNormalizer.FromParts(messageId, plainText, null, recipients);

        var htmlText = await FetchTextPartAsync(folder, uid, summary.HtmlBody, cancellationToken);

        if (!string.IsNullOrWhiteSpace(htmlText))
            return MessageBodyNormalizer.FromParts(messageId, null, htmlText, recipients);

        var message = await folder.GetMessageAsync(uid, cancellationToken);
        return MessageBodyNormalizer.FromMimeMessage(messageId, message);
    }

    private static async Task<string> FetchTextPartAsync(
        IMailFolder folder,
        UniqueId uid,
        BodyPartText part,
        CancellationToken cancellationToken)
    {
        if (part is null)
            return null;

        var entity = await folder.GetBodyPartAsync(uid, part, cancellationToken);
        return entity is TextPart textPart ? textPart.Text : null;
    }

    private static IReadOnlyList<MessageRecipient> ReadRecipients(Envelope envelope)
    {
        if (envelope is null)
            return [];

        var recipients = new List<MessageRecipient>();
        AddRecipients(recipients, "to", envelope.To);
        AddRecipients(recipients, "cc", envelope.Cc);
        AddRecipients(recipients, "bcc", envelope.Bcc);
        AddRecipients(recipients, "reply_to", envelope.ReplyTo);
        return recipients;
    }

    private static void AddRecipients(
        List<MessageRecipient> recipients,
        string type,
        InternetAddressList addresses)
    {
        if (addresses is null)
            return;

        foreach (var mailbox in addresses.Mailboxes)
        {
            if (string.IsNullOrWhiteSpace(mailbox.Address))
                continue;

            recipients.Add(new(
                Type: type,
                Name: string.IsNullOrWhiteSpace(mailbox.Name) ? null : mailbox.Name.Trim(),
                Email: mailbox.Address.Trim().ToLowerInvariant()));
        }
    }

    private static readonly FetchRequest BodyFetchRequest = new(
        MessageSummaryItems.UniqueId
        | MessageSummaryItems.Envelope
        | MessageSummaryItems.BodyStructure);

    private static readonly BodyBatchFetchResult EmptyBatchFetchResult = new([], 0, 0);

    private sealed record BodyBatchFetchResult(
        IReadOnlyList<MessageBodyContent> Bodies,
        int FetchedCount,
        int MissingCount);
}
