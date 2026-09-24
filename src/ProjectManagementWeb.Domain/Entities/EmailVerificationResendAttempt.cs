using ProjectManagementWeb.Domain.Enums;

namespace ProjectManagementWeb.Domain.Entities;

public sealed class EmailVerificationResendAttempt
{
    private EmailVerificationResendAttempt() { }

    public EmailVerificationResendAttempt(
        Guid id,
        Guid? accountId,
        string clientAddressHash,
        DateTimeOffset requestedAt,
        EmailVerificationResendOutcome outcome)
    {
        Id = id;
        AccountId = accountId;
        ClientAddressHash = clientAddressHash;
        RequestedAt = requestedAt;
        Outcome = outcome;
    }

    public Guid Id { get; private set; }
    public Guid? AccountId { get; private set; }
    public string ClientAddressHash { get; private set; } = string.Empty;
    public DateTimeOffset RequestedAt { get; private set; }
    public EmailVerificationResendOutcome Outcome { get; private set; }
}
