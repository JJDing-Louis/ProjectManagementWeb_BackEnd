namespace ProjectManagementWeb.Application.Users;

public sealed class BootstrapAdminPolicy
{
    public BootstrapAdminPolicy(string? account)
    {
        Account = string.IsNullOrWhiteSpace(account) ? null : account.Trim();
        NormalizedAccount = Account?.ToUpperInvariant();
    }

    public string? Account { get; }

    public string? NormalizedAccount { get; }

    public bool IsBootstrapAdmin(string? account) =>
        Account is not null && string.Equals(Account, account?.Trim(), StringComparison.OrdinalIgnoreCase);
}
