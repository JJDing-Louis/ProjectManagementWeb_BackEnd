namespace ProjectManagementWeb.Domain.Enums;

public enum EmailVerificationResendOutcome
{
    Allowed,
    NotEligible,
    CooldownLimited,
    AccountLimited,
    IpLimited
}
