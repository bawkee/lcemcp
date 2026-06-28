namespace LceMcp;

internal sealed record DatabaseStatus(
    int SchemaVersion,
    int TargetSchemaVersion,
    int AccountCount,
    int FolderCount,
    int MessageCount,
    int MessageLocationCount,
    int MessageBodyCount,
    int MessageSearchDocCount,
    int AttachmentCount,
    int AttachmentTextCount,
    int AttachmentSearchDocCount,
    SyncStateStatus LastSyncState,
    DatabaseInitializationKind InitializationKind,
    int OpenAttachmentExtractionFailureCount = 0,
    IReadOnlyDictionary<string, int> OpenAttachmentExtractionFailuresByCode = null);

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
