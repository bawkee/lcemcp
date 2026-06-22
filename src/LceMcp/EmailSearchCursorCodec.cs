using System.Text;
using System.Text.Json;

namespace LceMcp;

internal sealed record EmailSearchCursor(
    double Score,
    string Date,
    int MessageId);

internal static class EmailSearchCursorCodec
{
    public static EmailSearchCursor Decode(string cursor)
    {
        if (string.IsNullOrWhiteSpace(cursor))
            return null;

        try
        {
            var padded = cursor.Trim().Replace('-', '+').Replace('_', '/');
            padded = padded.PadRight(padded.Length + ((4 - padded.Length % 4) % 4), '=');
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(padded));
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;

            if (!root.TryGetProperty("score", out var scoreProperty)
                || scoreProperty.ValueKind != JsonValueKind.Number
                || !scoreProperty.TryGetDouble(out var score)
                || !root.TryGetProperty("message_id", out var messageIdProperty)
                || messageIdProperty.ValueKind != JsonValueKind.Number
                || !messageIdProperty.TryGetInt32(out var messageId))
                throw new CliException("Invalid email_search cursor.", 2);

            var date = root.TryGetProperty("date", out var dateProperty)
                && dateProperty.ValueKind == JsonValueKind.String
                    ? dateProperty.GetString()
                    : "";

            return new(score, date ?? "", messageId);
        }
        catch (CliException)
        {
            throw;
        }
        catch
        {
            throw new CliException("Invalid email_search cursor.", 2);
        }
    }

    public static string Encode(EmailSearchResult result)
    {
        if (result is null)
            return null;

        return Encode(result.Score, result.Date, result.MessageId);
    }

    public static string Encode(double score, string date, int messageId)
    {
        var json = JsonSerializer.Serialize(new
        {
            score,
            date = date ?? "",
            message_id = messageId
        });
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(json))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }
}
