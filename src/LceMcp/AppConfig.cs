namespace LceMcp;

internal sealed class AppConfig
{
    public int Version { get; set; } = 1;
    public List<AccountConfig> Accounts { get; } = [];

    public AccountConfig FindAccount(string idOrEmail)
    {
        return Accounts.FirstOrDefault(account =>
            account.Id.Equals(idOrEmail, StringComparison.OrdinalIgnoreCase)
            || account.EmailAddress.Equals(idOrEmail, StringComparison.OrdinalIgnoreCase));
    }

    public AccountConfig FindAccountByEmail(string email)
    {
        return Accounts.FirstOrDefault(account =>
            account.EmailAddress.Equals(email, StringComparison.OrdinalIgnoreCase));
    }

    public void UpsertAccount(AccountConfig account)
    {
        var index = Accounts.FindIndex(existing =>
            existing.Id.Equals(account.Id, StringComparison.OrdinalIgnoreCase)
            || existing.EmailAddress.Equals(account.EmailAddress, StringComparison.OrdinalIgnoreCase));

        if (index >= 0)
            Accounts[index] = account;
        else
            Accounts.Add(account);
    }
}
