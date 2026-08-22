namespace ProjectManagementWeb.Domain.Entities;

public sealed class RefreshToken
{
    private RefreshToken() { }

    public RefreshToken(Guid id, Guid accountId, Guid familyId, string tokenHash, DateTimeOffset now, DateTimeOffset expiresAt)
    {
        Id = id;
        AccountId = accountId;
        FamilyId = familyId;
        TokenHash = tokenHash;
        CreatedAt = now;
        ExpiresAt = expiresAt;
    }

    public Guid Id { get; private set; }
    public Guid AccountId { get; private set; }
    public Guid FamilyId { get; private set; }
    public string TokenHash { get; private set; } = string.Empty;
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset ExpiresAt { get; private set; }
    public DateTimeOffset? RevokedAt { get; private set; }
    public Guid? ReplacedByTokenId { get; private set; }

    public bool IsActive(DateTimeOffset now) => RevokedAt is null && ExpiresAt > now;

    public void Revoke(DateTimeOffset now, Guid? replacedByTokenId = null)
    {
        RevokedAt ??= now;
        ReplacedByTokenId = replacedByTokenId;
    }
}
