namespace ProjectManagementWeb.Domain.Entities;

public sealed class EmailVerificationToken
{
    private EmailVerificationToken() { }

    public EmailVerificationToken(
        Guid id,
        Guid accountId,
        Guid emailMessageId,
        string tokenHash,
        DateTimeOffset createdAt,
        DateTimeOffset expiresAt)
    {
        Id = id;
        AccountId = accountId;
        EmailMessageId = emailMessageId;
        TokenHash = tokenHash;
        CreatedAt = createdAt;
        ExpiresAt = expiresAt;
    }

    public Guid Id { get; private set; }
    public Guid AccountId { get; private set; }
    public Guid EmailMessageId { get; private set; }
    public string TokenHash { get; private set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset ExpiresAt { get; private set; }
    public DateTimeOffset? ActivatedAt { get; private set; }
    public DateTimeOffset? UsedAt { get; private set; }
    public DateTimeOffset? InvalidatedAt { get; private set; }

    public bool IsActive(DateTimeOffset now) =>
        ActivatedAt is not null &&
        UsedAt is null &&
        InvalidatedAt is null &&
        ExpiresAt > now;

    public void Activate(DateTimeOffset now)
    {
        if (ActivatedAt is not null || InvalidatedAt is not null)
        {
            throw new InvalidOperationException("Email 驗證 Token 已完成生命週期狀態設定。");
        }

        ActivatedAt = now;
    }

    public void Use(DateTimeOffset now)
    {
        if (!IsActive(now))
        {
            throw new InvalidOperationException("Email 驗證 Token 不是可使用狀態。");
        }

        UsedAt = now;
    }

    public void Invalidate(DateTimeOffset now)
    {
        if (UsedAt is null)
        {
            InvalidatedAt ??= now;
        }
    }
}
