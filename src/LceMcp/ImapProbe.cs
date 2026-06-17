using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using MailKit.Security;
using MimeKit;

namespace LceMcp;

internal sealed class ImapProbe
{
    public async Task RunAsync(
        AccountConfig account,
        string password,
        ImapProbeOptions options,
        CancellationToken cancellationToken)
    {
        if (options.Limit < 1 || options.Limit > 100)
            throw new CliException("--limit must be between 1 and 100.", 2);

        if (options.SinceDays < 0)
            throw new CliException("--since-days must be 0 or greater.", 2);

        using var client = new ImapClient();
        client.Timeout = 30_000;

        var socketOptions = ResolveSocketOptions(account.ImapSecurity);
        Console.WriteLine($"Connecting to {account.ImapHost}:{account.ImapPort} ({account.ImapSecurity})...");
        await client.ConnectAsync(account.ImapHost, account.ImapPort, socketOptions, cancellationToken);
        Console.WriteLine($"Connected. Capabilities: {client.Capabilities}");

        client.AuthenticationMechanisms.Remove("XOAUTH2");

        Console.WriteLine($"Authenticating as {account.Username}...");
        await client.AuthenticateAsync(account.Username, password, cancellationToken);
        Console.WriteLine("Authenticated.");

        var folders = await GetFoldersAsync(client, cancellationToken);
        PrintFolders(folders);

        var folder = await ResolveFolderAsync(client, folders, options.Folder, cancellationToken);
        await folder.OpenAsync(FolderAccess.ReadOnly, cancellationToken);
        Console.WriteLine($"Opened folder: {folder.FullName}");
        Console.WriteLine($"Messages: {folder.Count}; Recent: {folder.Recent}; UID validity: {folder.UidValidity}");

        var searchQuery = BuildSearchQuery(options);
        Console.WriteLine(SearchDescription(options));
        var uids = await folder.SearchAsync(searchQuery, cancellationToken);
        var selectedUids = uids.Reverse().Take(options.Limit).ToList();

        Console.WriteLine($"Matched UIDs: {uids.Count}; fetching newest {selectedUids.Count} summaries.");

        if (selectedUids.Count == 0)
        {
            await client.DisconnectAsync(true, cancellationToken);
            return;
        }

        var request = new FetchRequest(
            MessageSummaryItems.UniqueId
            | MessageSummaryItems.Envelope
            | MessageSummaryItems.Flags
            | MessageSummaryItems.InternalDate
            | MessageSummaryItems.Size
            | MessageSummaryItems.BodyStructure);

        var summaries = await folder.FetchAsync(selectedUids, request, cancellationToken);
        foreach (var summary in summaries.OrderByDescending(summary => summary.UniqueId.Id))
            PrintSummary(summary);

        if (options.FetchFirstBody)
        {
            var newest = selectedUids.First();
            var message = await folder.GetMessageAsync(newest, cancellationToken);
            PrintBodyPreview(message, options.BodyChars);
        }

        await client.DisconnectAsync(true, cancellationToken);
        Console.WriteLine("Disconnected.");
    }

    private static SecureSocketOptions ResolveSocketOptions(string security)
    {
        return security.ToLowerInvariant() switch
        {
            "ssl" or "ssl/tls" => SecureSocketOptions.SslOnConnect,
            "starttls" => SecureSocketOptions.StartTls,
            "none" => SecureSocketOptions.None,
            _ => throw new CliException($"Unsupported IMAP security value: {security}", 2)
        };
    }

    private static async Task<List<IMailFolder>> GetFoldersAsync(ImapClient client, CancellationToken cancellationToken)
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

    private static void PrintFolders(IReadOnlyCollection<IMailFolder> folders)
    {
        Console.WriteLine($"Folders discovered: {folders.Count}");

        foreach (var folder in folders.Take(25))
            Console.WriteLine($"  {folder.FullName}  attrs={string.Join(",", folder.Attributes)}");

        if (folders.Count > 25)
            Console.WriteLine($"  ... {folders.Count - 25} more");
    }

    private static async Task<IMailFolder> ResolveFolderAsync(
        ImapClient client,
        IReadOnlyCollection<IMailFolder> folders,
        string folderPath,
        CancellationToken cancellationToken)
    {
        if (folderPath.Equals("INBOX", StringComparison.OrdinalIgnoreCase))
            return client.Inbox;

        var discovered = folders.FirstOrDefault(folder =>
            folder.FullName.Equals(folderPath, StringComparison.OrdinalIgnoreCase)
            || folder.Name.Equals(folderPath, StringComparison.OrdinalIgnoreCase));

        if (discovered is not null)
            return discovered;

        return await client.GetFolderAsync(folderPath, cancellationToken);
    }

    private static SearchQuery BuildSearchQuery(ImapProbeOptions options)
    {
        var query = SearchQuery.All;

        if (options.SinceDays > 0)
            query = query.And(SearchQuery.DeliveredAfter(DateTime.Now.AddDays(-options.SinceDays)));

        if (!string.IsNullOrWhiteSpace(options.Query))
        {
            var text = options.Query.Trim();
            var textQuery = SearchQuery.FromContains(text)
                .Or(SearchQuery.SubjectContains(text))
                .Or(SearchQuery.BodyContains(text));

            query = query.And(textQuery);
        }

        return query;
    }

    private static string SearchDescription(ImapProbeOptions options)
    {
        var dateBound = options.SinceDays > 0
            ? $"delivered in the last {options.SinceDays} days"
            : "no date bound";

        if (string.IsNullOrWhiteSpace(options.Query))
            return $"Searching messages with {dateBound}.";

        return $"Searching from/subject/body for '{options.Query}' with {dateBound}.";
    }

    private static void PrintSummary(IMessageSummary summary)
    {
        var envelope = summary.Envelope;
        var from = FormatAddresses(envelope?.From);
        var subject = string.IsNullOrWhiteSpace(envelope?.Subject) ? "(no subject)" : envelope.Subject;
        var date = summary.InternalDate?.ToString("u") ?? envelope?.Date?.ToString("u") ?? "(no date)";
        var attachments = summary.Attachments?.Count() ?? 0;

        Console.WriteLine();
        Console.WriteLine($"UID {summary.UniqueId.Id}  {date}  size={summary.Size}");
        Console.WriteLine($"From: {from}");
        Console.WriteLine($"Subject: {subject}");
        Console.WriteLine($"Flags: {summary.Flags?.ToString() ?? "(none)"}; Attachments seen in body structure: {attachments}");
    }

    private static string FormatAddresses(InternetAddressList addresses)
    {
        if (addresses is null || addresses.Count == 0)
            return "(unknown)";

        return string.Join(", ", addresses.Mailboxes.Select(mailbox =>
            string.IsNullOrWhiteSpace(mailbox.Name)
                ? mailbox.Address
                : $"{mailbox.Name} <{mailbox.Address}>"));
    }

    private static void PrintBodyPreview(MimeMessage message, int bodyChars)
    {
        var text = message.TextBody ?? HtmlToRoughText(message.HtmlBody) ?? "";
        if (text.Length > bodyChars)
            text = text[..bodyChars] + "\n[clipped]";

        Console.WriteLine();
        Console.WriteLine("First result body preview:");
        Console.WriteLine(text);
    }

    private static string HtmlToRoughText(string html)
    {
        if (string.IsNullOrWhiteSpace(html))
            return null;

        return System.Text.RegularExpressions.Regex.Replace(html, "<[^>]+>", " ");
    }
}
