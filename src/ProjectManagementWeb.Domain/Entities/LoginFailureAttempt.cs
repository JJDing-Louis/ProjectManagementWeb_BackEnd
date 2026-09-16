using ProjectManagementWeb.Domain.Enums;

namespace ProjectManagementWeb.Domain.Entities;

public sealed class LoginFailureAttempt
{
    private LoginFailureAttempt() { }

    public LoginFailureAttempt(
        Guid id,
        string accountKeyHash,
        string clientAddressHash,
        DateTimeOffset occurredAt,
        LoginFailureOutcome outcome)
    {
        Id = id;
        AccountKeyHash = accountKeyHash;
        ClientAddressHash = clientAddressHash;
        OccurredAt = occurredAt;
        Outcome = outcome;
    }

    public Guid Id { get; private set; }
    public string AccountKeyHash { get; private set; } = string.Empty;
    public string ClientAddressHash { get; private set; } = string.Empty;
    public DateTimeOffset OccurredAt { get; private set; }
    public LoginFailureOutcome Outcome { get; private set; }
}
