namespace ProjectManagementWeb.Infrastructure.Email;

internal interface IEmailGateway
{
    Task SendAsync(string recipient, string subject, string body, CancellationToken cancellationToken);
}
