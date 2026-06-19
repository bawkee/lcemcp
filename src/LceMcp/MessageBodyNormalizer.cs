using System.Net;
using System.Text.RegularExpressions;
using MimeKit;

namespace LceMcp;

internal static class MessageBodyNormalizer
{
    public static MessageBodyContent FromMimeMessage(int messageId, MimeMessage message)
    {
        var plainText = BlankToNull(message.TextBody);
        var htmlText = BlankToNull(message.HtmlBody);
        var normalizedText = NormalizeText(plainText ?? HtmlToText(htmlText));

        return new(
            MessageId: messageId,
            PlainText: plainText,
            HtmlText: htmlText,
            NormalizedText: normalizedText,
            Recipients: ReadRecipients(message));
    }

    private static IReadOnlyList<MessageRecipient> ReadRecipients(MimeMessage message)
    {
        var recipients = new List<MessageRecipient>();

        AddRecipients(recipients, "to", message.To);
        AddRecipients(recipients, "cc", message.Cc);
        AddRecipients(recipients, "bcc", message.Bcc);
        AddRecipients(recipients, "reply_to", message.ReplyTo);

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
            var email = BlankToNull(mailbox.Address);
            if (email is null)
                continue;

            recipients.Add(new(
                Type: type,
                Name: BlankToNull(mailbox.Name),
                Email: email.ToLowerInvariant()));
        }
    }

    private static string HtmlToText(string html)
    {
        if (string.IsNullOrWhiteSpace(html))
            return null;

        var withoutScripts = ScriptOrStyleRegex.Replace(html, " ");
        var withBreaks = BlockBreakRegex.Replace(withoutScripts, "\n");
        var withoutTags = TagRegex.Replace(withBreaks, " ");
        return WebUtility.HtmlDecode(withoutTags);
    }

    private static string NormalizeText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return null;

        var normalized = LineBreakRegex.Replace(text.Replace("\r\n", "\n"), "\n");
        normalized = HorizontalWhitespaceRegex.Replace(normalized, " ");
        normalized = ExcessiveBlankLinesRegex.Replace(normalized, "\n\n");
        normalized = normalized.Trim();

        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    private static string BlankToNull(string value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static readonly Regex ScriptOrStyleRegex = new(
        @"<(script|style)\b[^>]*>.*?</\1>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex BlockBreakRegex = new(
        @"</?(br|p|div|li|tr|table|h[1-6])\b[^>]*>",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex TagRegex = new(
        "<[^>]+>",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex LineBreakRegex = new(
        @"[ \t]*\n[ \t]*",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex HorizontalWhitespaceRegex = new(
        @"[^\S\n]+",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex ExcessiveBlankLinesRegex = new(
        @"\n{3,}",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
}
