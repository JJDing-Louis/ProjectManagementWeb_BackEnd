namespace ProjectManagementWeb.Infrastructure.Email;

internal interface IEmailGateway
{
    Task<EmailSendResult> SendAsync(
        string recipient,
        string subject,
        string body,
        string idempotencyKey,
        CancellationToken cancellationToken);
}

internal sealed record EmailSendResult(string ResponseId);
