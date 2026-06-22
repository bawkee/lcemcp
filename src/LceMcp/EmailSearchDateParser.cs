using System.Globalization;
using System.Text.RegularExpressions;

namespace LceMcp;

internal static partial class EmailSearchDateParser
{
    public static string NormalizeLowerBound(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var trimmed = value.Trim();
        if (DateOnlyRegex().IsMatch(trimmed))
        {
            var date = DateOnly.ParseExact(trimmed, "yyyy-MM-dd", CultureInfo.InvariantCulture);
            return new DateTimeOffset(date.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc)).ToString("O");
        }

        return NormalizeDateTimeOffset(trimmed);
    }

    public static string NormalizeUpperBound(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var trimmed = value.Trim();
        if (DateOnlyRegex().IsMatch(trimmed))
        {
            var date = DateOnly.ParseExact(trimmed, "yyyy-MM-dd", CultureInfo.InvariantCulture);
            return new DateTimeOffset(date.ToDateTime(TimeOnly.MaxValue, DateTimeKind.Utc)).ToString("O");
        }

        return NormalizeDateTimeOffset(trimmed);
    }

    public static void ValidateRange(string dateFrom, string dateTo)
    {
        if (dateFrom is null || dateTo is null)
            return;

        if (DateTimeOffset.Parse(dateFrom, CultureInfo.InvariantCulture) >
            DateTimeOffset.Parse(dateTo, CultureInfo.InvariantCulture))
            throw new CliException("date_from must be earlier than or equal to date_to.", 2);
    }

    private static string NormalizeDateTimeOffset(string value)
    {
        if (!DateTimeOffset.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var parsed))
            throw new CliException($"Invalid search date: {value}", 2);

        return parsed.ToUniversalTime().ToString("O");
    }

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}$", RegexOptions.CultureInvariant)]
    private static partial Regex DateOnlyRegex();
}
