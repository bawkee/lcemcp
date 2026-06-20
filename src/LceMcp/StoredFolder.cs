namespace LceMcp;

internal sealed record StoredFolder(
    int Id,
    int AccountId,
    string AccountName,
    string AccountEmailAddress,
    string Name,
    string Path,
    string Delimiter,
    string Attributes,
    string Role,
    bool Selectable,
    bool SyncEnabled,
    string UidValidity,
    int? MessageCount,
    int? RecentCount,
    string LastDiscoveredAt,
    string LastSyncAt);
