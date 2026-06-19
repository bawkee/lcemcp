namespace LceMcp;

internal sealed record DatabaseStatus(
    string SchemaMode,
    bool MigrationsLocked,
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
    RecreatedPrototype,
    Migrated
}

internal sealed record SyncStateStatus(
    string AccountName,
    string FolderPath,
    string LastSuccessAt,
    string LastErrorAt,
    string LastError);
