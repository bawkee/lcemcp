namespace LceMcp;

internal sealed record DatabaseMigration(int Version, string Name, string Sql);
