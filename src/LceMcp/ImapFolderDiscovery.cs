using MailKit;
using MailKit.Net.Imap;
using MailKit.Security;

namespace LceMcp;

internal sealed class ImapFolderDiscovery
{
    public async Task<ImapFolderDiscoveryResult> DiscoverAsync(
        AccountConfig account,
        string password,
        CancellationToken cancellationToken)
    {
        using var client = new ImapClient();
        client.Timeout = 30_000;

        await client.ConnectAsync(
            account.ImapHost,
            account.ImapPort,
            ResolveSocketOptions(account.ImapSecurity),
            cancellationToken);

        client.AuthenticationMechanisms.Remove("XOAUTH2");
        await client.AuthenticateAsync(account.Username, password, cancellationToken);

        var capabilities = client.Capabilities.ToString();
        var folders = await GetFoldersAsync(client, cancellationToken);
        var folderInfos = new List<ImapFolderInfo>(folders.Count);

        foreach (var folder in folders)
            folderInfos.Add(await ReadFolderInfoAsync(folder, cancellationToken));

        await client.DisconnectAsync(true, cancellationToken);
        return new(capabilities, folderInfos);
    }

    public static SecureSocketOptions ResolveSocketOptions(string security)
    {
        return security.ToLowerInvariant() switch
        {
            "ssl" or "ssl/tls" => SecureSocketOptions.SslOnConnect,
            "starttls" => SecureSocketOptions.StartTls,
            "none" => SecureSocketOptions.None,
            _ => throw new CliException($"Unsupported IMAP security value: {security}", 2)
        };
    }

    public static async Task<List<IMailFolder>> GetFoldersAsync(
        ImapClient client,
        CancellationToken cancellationToken)
    {
        var folders = new List<IMailFolder>();
        foreach (var ns in client.PersonalNamespaces)
        {
            var namespaceFolders = await client.GetFoldersAsync(ns, subscribedOnly: false, cancellationToken);
            folders.AddRange(namespaceFolders);
        }

        return folders
            .OrderBy(folder => folder.FullName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static async Task<IMailFolder> ResolveFolderAsync(
        ImapClient client,
        IReadOnlyCollection<IMailFolder> folders,
        string folderPath,
        CancellationToken cancellationToken)
    {
        var discovered = folders.FirstOrDefault(folder =>
            folder.FullName.Equals(folderPath, StringComparison.OrdinalIgnoreCase)
            || folder.Name.Equals(folderPath, StringComparison.OrdinalIgnoreCase));

        if (discovered is not null)
            return discovered;

        if (folderPath.Equals("INBOX", StringComparison.OrdinalIgnoreCase))
            return client.Inbox;

        return await client.GetFolderAsync(folderPath, cancellationToken);
    }

    private static async Task<ImapFolderInfo> ReadFolderInfoAsync(
        IMailFolder folder,
        CancellationToken cancellationToken)
    {
        var selectable = !folder.Attributes.HasFlag(FolderAttributes.NoSelect);
        string uidValidity = null;
        int? messageCount = null;
        int? recentCount = null;
        string statusError = null;

        if (selectable)
        {
            try
            {
                await folder.StatusAsync(
                    StatusItems.Count | StatusItems.Recent | StatusItems.UidValidity,
                    cancellationToken);

                uidValidity = folder.UidValidity.ToString();
                messageCount = folder.Count;
                recentCount = folder.Recent;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                statusError = ex.Message;
            }
        }

        return new(
            FullName: folder.FullName,
            Name: folder.Name,
            Delimiter: FormatDelimiter(folder.DirectorySeparator),
            Attributes: folder.Attributes.ToString(),
            Role: ImapFolderRoles.Infer(folder.Attributes),
            Selectable: selectable,
            UidValidity: uidValidity,
            MessageCount: messageCount,
            RecentCount: recentCount,
            StatusError: statusError);
    }

    private static string FormatDelimiter(char delimiter)
    {
        return delimiter == '\0' ? null : delimiter.ToString();
    }
}
