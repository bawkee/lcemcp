using System.Text.RegularExpressions;

namespace LceMcp;

internal static class FtsQueryBuilder
{
    public static string Build(string query)
    {
        if (string.IsNullOrWhiteSpace(query))
            throw new CliException("Search query cannot be empty.", 2);

        var items = new List<string>();

        foreach (Match match in QueryTokenRegex.Matches(query))
        {
            var raw = match.Value.Trim();
            if (raw.Length == 0)
                continue;

            if (IsOperator(raw))
            {
                AddOperator(items, raw.ToUpperInvariant());
                continue;
            }

            var term = raw.StartsWith('"') && raw.EndsWith('"') && raw.Length >= 2
                ? BuildPhrase(raw[1..^1])
                : BuildTerm(raw);

            if (term is null)
                continue;

            AddTerm(items, term);
        }

        while (items.Count > 0 && IsOperator(items[^1]))
            items.RemoveAt(items.Count - 1);

        if (items.Count == 0)
            throw new CliException("Search query did not contain any searchable terms.", 2);

        return string.Join(' ', items);
    }

    private static void AddOperator(List<string> items, string op)
    {
        if (items.Count == 0 || IsOperator(items[^1]))
            return;

        items.Add(op);
    }

    private static void AddTerm(List<string> items, string term)
    {
        if (items.Count > 0 && !IsOperator(items[^1]))
            items.Add("AND");

        items.Add(term);
    }

    private static string BuildPhrase(string value)
    {
        var terms = ExtractTerms(value);
        return terms.Count switch
        {
            0 => null,
            1 => terms[0],
            _ => $"\"{string.Join(' ', terms)}\""
        };
    }

    private static string BuildTerm(string value)
    {
        var terms = ExtractTerms(value);
        return terms.Count switch
        {
            0 => null,
            1 => terms[0],
            _ => $"\"{string.Join(' ', terms)}\""
        };
    }

    private static List<string> ExtractTerms(string value) =>
        SearchTermRegex.Matches(value.ToLowerInvariant())
            .Select(match => match.Value)
            .Where(term => !string.IsNullOrWhiteSpace(term))
            .ToList();

    private static bool IsOperator(string value) =>
        value.Equals("AND", StringComparison.OrdinalIgnoreCase)
        || value.Equals("OR", StringComparison.OrdinalIgnoreCase);

    private static readonly Regex QueryTokenRegex = new(
        @"""[^""]+""|\S+",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex SearchTermRegex = new(
        @"[\p{L}\p{N}_]+",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);
}
