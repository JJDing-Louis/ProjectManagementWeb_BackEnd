using ProjectManagementWeb.Domain.Enums;

namespace ProjectManagementWeb.Domain.Entities;

public sealed class EmailMessage
{
    private EmailMessage() { }

    public EmailMessage(Guid id, string recipient, string subject, string body, DateTimeOffset createdAt)
    {
        Id = id;
        Recipient = recipient;
        Subject = subject;
        Body = body;
        CreatedAt = createdAt;
        Status = EmailDeliveryStatus.Pending;
    }

    public Guid Id { get; private set; }
    public string Recipient { get; private set; } = string.Empty;
    public string Subject { get; private set; } = string.Empty;
    public string Body { get; private set; } = string.Empty;
    public EmailDeliveryStatus Status { get; private set; }
    public int AttemptCount { get; private set; }
    public string? LastError { get; private set; }
    public DateTimeOffset CreatedAt { get; private set; }
    public DateTimeOffset? SentAt { get; private set; }

    public void MarkSent(DateTimeOffset now)
    {
        AttemptCount++;
        Status = EmailDeliveryStatus.Sent;
        SentAt = now;
        LastError = null;
    }

    public void MarkFailed(string error)
    {
        AttemptCount++;
        Status = EmailDeliveryStatus.Failed;
        LastError = error;
    }
}
