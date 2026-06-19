namespace LceMcp;

internal sealed record DatabaseStatus(
    int SchemaVersion,
    int TargetSchemaVersion,
    int AccountCount,
    int FolderCount,
    int MessageCount,
    int MessageLocationCount,
    SyncStateStatus LastSyncState,
    DatabaseInitializationKind InitializationKind);

internal enum DatabaseInitializationKind
{
    Opened,
    Created,
    Migrated
}

internal sealed record SyncStateStatus(
    string AccountName,
    string FolderPath,
    string LastSuccessAt,
    string LastErrorAt,
    string LastError);
