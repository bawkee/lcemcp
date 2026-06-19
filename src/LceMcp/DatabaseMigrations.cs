namespace LceMcp;

internal static class DatabaseMigrations
{
    // Prototype guard: keep this true until the MVP schema is stable enough to preserve.
    // While locked, leave All empty; schema changes rebuild email.db instead of migrating it.
    public const bool MigrationsLocked = true;
    public const int PrototypeSchemaVersion = 2;
    public const string PrototypeSchemaName = "prototype_schema_metadata_sync";

    public static IReadOnlyList<DatabaseMigration> All { get; } = [];

    public static int TargetVersion => MigrationsLocked
        ? PrototypeSchemaVersion
        : All.Select(migration => migration.Version).DefaultIfEmpty(0).Max();
}
