using System.Collections.Concurrent;
using ProjectManagementWeb.Infrastructure.Email;

namespace ProjectManagementWeb.IntegrationTests;

internal sealed class TestEmailGateway : IEmailGateway
{
    private readonly ConcurrentQueue<string> _recipients = new();
    private readonly ConcurrentQueue<string> _bodies = new();
    private readonly ConcurrentQueue<string> _idempotencyKeys = new();

    public bool ShouldFail { get; set; }

    public IReadOnlyCollection<string> Recipients => _recipients.ToArray();
    public IReadOnlyCollection<string> Bodies => _bodies.ToArray();
    public IReadOnlyCollection<string> IdempotencyKeys => _idempotencyKeys.ToArray();

    public Task<EmailSendResult> SendAsync(
        string recipient,
        string subject,
        string body,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _recipients.Enqueue(recipient);
        _bodies.Enqueue(body);
        _idempotencyKeys.Enqueue(idempotencyKey);
        return ShouldFail
            ? Task.FromException<EmailSendResult>(new InvalidOperationException("測試用 Email gateway 失敗。"))
            : Task.FromResult(new EmailSendResult($"test-response-{idempotencyKey}"));
    }

    public void Reset()
    {
        ShouldFail = false;
        while (_recipients.TryDequeue(out _))
        {
        }
        while (_bodies.TryDequeue(out _))
        {
        }
        while (_idempotencyKeys.TryDequeue(out _))
        {
        }
    }
}
