namespace LceMcp;

internal sealed record AccountSyncSummary(
    int AccountId,
    string AccountName,
    string EmailAddress,
    string LastSuccessAt,
    string LastErrorAt,
    string LastError);
