namespace LceMcp;

internal sealed record ImapFolderDiscoveryResult(
    string Capabilities,
    IReadOnlyList<ImapFolderInfo> Folders);

internal sealed record ImapFolderInfo(
    string FullName,
    string Name,
    string Delimiter,
    string Attributes,
    string Role,
    bool Selectable,
    string UidValidity,
    int? MessageCount,
    int? RecentCount,
    string StatusError);
