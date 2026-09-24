using MailKit.Net.Smtp;
using MailKit.Security;
using Microsoft.Extensions.Options;
using MimeKit;
using ProjectManagementWeb.Infrastructure.Configuration;

namespace ProjectManagementWeb.Infrastructure.Email;

internal sealed class SmtpEmailGateway : IEmailGateway
{
    private readonly SmtpOptions _options;

    public SmtpEmailGateway(IOptions<SmtpOptions> options) => _options = options.Value;

    public async Task<EmailSendResult> SendAsync(
        string recipient,
        string subject,
        string body,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_options.UserName) || string.IsNullOrWhiteSpace(_options.Password))
        {
            throw new InvalidOperationException("尚未設定 SMTP 帳號與密碼。");
        }

        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(_options.FromName, _options.FromAddress));
        message.To.Add(MailboxAddress.Parse(recipient));
        message.Subject = subject;
        message.Body = new TextPart("html") { Text = body };
        message.MessageId = $"<{idempotencyKey}@projectmanagementweb.local>";
        message.Headers.Add("X-Idempotency-Key", idempotencyKey);

        using var client = new SmtpClient
        {
            CheckCertificateRevocation = _options.CheckCertificateRevocation
        };
        await client.ConnectAsync(_options.Host, _options.Port, SecureSocketOptions.StartTls, cancellationToken);
        await client.AuthenticateAsync(_options.UserName, _options.Password, cancellationToken);
        string response = await client.SendAsync(message, cancellationToken);
        await client.DisconnectAsync(true, cancellationToken);
        return new EmailSendResult(string.IsNullOrWhiteSpace(response) ? message.MessageId : response);
    }
}
