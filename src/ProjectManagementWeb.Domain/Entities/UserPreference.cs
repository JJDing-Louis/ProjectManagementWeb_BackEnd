namespace ProjectManagementWeb.Domain.Entities;

public sealed class UserPreference
{
    private UserPreference() { }

    public UserPreference(Guid accountId) => AccountId = accountId;

    public Guid AccountId { get; private set; }
    public bool SkipBatchConfirmation { get; private set; }

    public void Update(bool skipBatchConfirmation) => SkipBatchConfirmation = skipBatchConfirmation;
}
