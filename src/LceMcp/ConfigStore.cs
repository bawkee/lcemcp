using System.Text;
using Tomlyn;
using Tomlyn.Model;

namespace LceMcp;

internal sealed class ConfigStore
{
    private readonly AppPaths _paths;

    public ConfigStore(AppPaths paths)
    {
        _paths = paths;
    }

    public string ConfigPath => _paths.ConfigPath;

    public AppConfig Load()
    {
        if (!File.Exists(_paths.ConfigPath))
            return new AppConfig();

        var text = File.ReadAllText(_paths.ConfigPath, Encoding.UTF8);
        var table = TomlSerializer.Deserialize<TomlTable>(text);
        var config = new AppConfig
        {
            Version = GetInt(table, "version", 1)
        };

        if (table.TryGetValue("ocr", out var ocrValue)
            && ocrValue is TomlTable ocr)
        {
            config.Ocr.Enabled = GetBool(ocr, "enabled", false);
            config.Ocr.AutoDownloadLanguagePacks = GetBool(ocr, "auto_download_language_packs", true);
            config.Ocr.FallbackScript = GetString(ocr, "fallback_script", "Latin");
            config.Ocr.Languages.AddRange(GetStringArray(ocr, "languages"));
        }

        if (table.TryGetValue("account", out var accountsValue)
            && accountsValue is TomlTableArray accounts)
        {
            foreach (var item in accounts)
            {
                config.Accounts.Add(new AccountConfig
                {
                    Id = GetString(item, "id"),
                    DisplayName = GetString(item, "display_name"),
                    EmailAddress = GetString(item, "email_address"),
                    Provider = GetString(item, "provider", "custom"),
                    Username = GetString(item, "username"),
                    ImapHost = GetString(item, "imap_host"),
                    ImapPort = GetInt(item, "imap_port", 993),
                    ImapSecurity = GetString(item, "imap_security", "ssl"),
                    HistoryDays = GetInt(item, "history_days", 90),
                    AttachmentPolicy = GetString(item, "attachment_policy", "metadata_only"),
                    CredentialRef = GetString(item, "credential_ref"),
                    Enabled = GetBool(item, "enabled", true)
                });
            }
        }

        return config;
    }

    public void Save(AppConfig config)
    {
        Directory.CreateDirectory(_paths.ConfigDirectory);

        var builder = new StringBuilder();
        builder.AppendLine("# lcemcp local config. Passwords are stored in the OS credential store.");
        builder.AppendLine($"version = {config.Version}");
        builder.AppendLine();
        builder.AppendLine("[ocr]");
        builder.AppendLine($"enabled = {config.Ocr.Enabled.ToString().ToLowerInvariant()}");
        builder.AppendLine($"auto_download_language_packs = {config.Ocr.AutoDownloadLanguagePacks.ToString().ToLowerInvariant()}");
        AppendString(builder, "fallback_script", config.Ocr.FallbackScript);
        builder.Append("languages = [")
            .Append(string.Join(", ", config.Ocr.Languages.Select(TomlString)))
            .AppendLine("]");
        builder.AppendLine();

        foreach (var account in config.Accounts.OrderBy(account => account.Id, StringComparer.OrdinalIgnoreCase))
        {
            builder.AppendLine("[[account]]");
            AppendString(builder, "id", account.Id);
            AppendString(builder, "display_name", account.DisplayName);
            AppendString(builder, "email_address", account.EmailAddress);
            AppendString(builder, "provider", account.Provider);
            AppendString(builder, "username", account.Username);
            AppendString(builder, "imap_host", account.ImapHost);
            builder.AppendLine($"imap_port = {account.ImapPort}");
            AppendString(builder, "imap_security", account.ImapSecurity);
            builder.AppendLine($"history_days = {account.HistoryDays}");
            AppendString(builder, "attachment_policy", account.AttachmentPolicy);
            AppendString(builder, "credential_ref", account.CredentialRef);
            builder.AppendLine($"enabled = {account.Enabled.ToString().ToLowerInvariant()}");
            builder.AppendLine();
        }

        File.WriteAllText(_paths.ConfigPath, builder.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    private static void AppendString(StringBuilder builder, string name, string value)
    {
        builder.Append(name).Append(" = ").Append('"').Append(EscapeTomlString(value ?? "")).AppendLine("\"");
    }

    private static string GetString(TomlTable table, string name, string defaultValue = "")
    {
        return table.TryGetValue(name, out var value) && value is string text ? text : defaultValue;
    }

    private static int GetInt(TomlTable table, string name, int defaultValue)
    {
        if (!table.TryGetValue(name, out var value))
            return defaultValue;

        return value switch
        {
            int intValue => intValue,
            long longValue when longValue <= int.MaxValue && longValue >= int.MinValue => (int)longValue,
            _ => defaultValue
        };
    }

    private static bool GetBool(TomlTable table, string name, bool defaultValue)
    {
        return table.TryGetValue(name, out var value) && value is bool boolValue ? boolValue : defaultValue;
    }

    private static IReadOnlyList<string> GetStringArray(TomlTable table, string name)
    {
        if (!table.TryGetValue(name, out var value) || value is not TomlArray array)
            return [];

        return array
            .OfType<string>()
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Select(item => item.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static string TomlString(string value) =>
        $"\"{EscapeTomlString(value ?? "")}\"";

    private static string EscapeTomlString(string value)
    {
        var builder = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            switch (ch)
            {
                case '\\':
                    builder.Append(@"\\");
                    break;
                case '"':
                    builder.Append("\\\"");
                    break;
                case '\b':
                    builder.Append(@"\b");
                    break;
                case '\t':
                    builder.Append(@"\t");
                    break;
                case '\n':
                    builder.Append(@"\n");
                    break;
                case '\f':
                    builder.Append(@"\f");
                    break;
                case '\r':
                    builder.Append(@"\r");
                    break;
                default:
                    if (char.IsControl(ch))
                        builder.Append(@"\u").Append(((int)ch).ToString("x4"));
                    else
                        builder.Append(ch);
                    break;
            }
        }

        return builder.ToString();
    }
}
