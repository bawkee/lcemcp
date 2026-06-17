namespace LceMcp;

internal sealed class CommandOptions
{
    private readonly Dictionary<string, string> _values = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _flags = new(StringComparer.OrdinalIgnoreCase);

    public static CommandOptions Parse(IEnumerable<string> args)
    {
        var parsed = new CommandOptions();
        var tokens = args.ToArray();

        for (var i = 0; i < tokens.Length; i++)
        {
            var token = tokens[i];
            if (!token.StartsWith("--", StringComparison.Ordinal))
                throw new CliException($"Unexpected positional argument: {token}", 2);

            var equals = token.IndexOf('=');
            if (equals >= 0)
            {
                var key = token[..equals];
                var value = token[(equals + 1)..];
                parsed._values[key] = value;
                continue;
            }

            if (i + 1 < tokens.Length && !tokens[i + 1].StartsWith("--", StringComparison.Ordinal))
            {
                parsed._values[token] = tokens[++i];
                continue;
            }

            parsed._flags.Add(token);
        }

        return parsed;
    }

    public bool Has(string key) => _flags.Contains(key) || _values.ContainsKey(key);

    public string Get(string key) => _values.TryGetValue(key, out var value) ? value : null;

    public string GetRequired(string key)
    {
        var value = Get(key);
        if (string.IsNullOrWhiteSpace(value))
            throw new CliException($"Missing required option: {key}", 2);

        return value;
    }

    public int GetInt(string key, int defaultValue)
    {
        var value = Get(key);
        if (string.IsNullOrWhiteSpace(value))
            return defaultValue;

        if (!int.TryParse(value, out var parsed))
            throw new CliException($"{key} must be an integer.", 2);

        return parsed;
    }
}
