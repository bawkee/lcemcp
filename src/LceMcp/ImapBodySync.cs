using MailKit;
using MailKit.Net.Imap;

namespace LceMcp;

internal sealed class ImapBodySync
{
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
        Action<int, int> progress = null)
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
                foreach (var target in batch)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if (!uint.TryParse(target.ProviderUid, out var uidValue))
                    {
                        missingCount++;
                        progress?.Invoke();
                        continue;
                    }

                    try
                    {
                        var message = await folder.GetMessageAsync(new UniqueId(uidValue), cancellationToken);
                        fetchedCount++;

                        var body = MessageBodyNormalizer.FromMimeMessage(target.MessageId, message);
                        _database.UpsertMessageBody(body);
                        persistedCount++;
                    }
                    catch (MessageNotFoundException)
                    {
                        missingCount++;
                    }

                    progress?.Invoke();
                }
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
}
