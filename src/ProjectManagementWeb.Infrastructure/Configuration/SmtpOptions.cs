namespace ProjectManagementWeb.Infrastructure.Configuration;

public sealed class SmtpOptions
{
    public const string SectionName = "Smtp";
    public string Host { get; set; } = "smtp.gmail.com";
    public int Port { get; set; } = 587;
    public string UserName { get; set; } = string.Empty;
    public string Password { get; set; } = string.Empty;
    public string FromAddress { get; set; } = string.Empty;
    public string FromName { get; set; } = "Project Management Web";
    public string FrontendBaseUrl { get; set; } = "http://localhost:5173";
}
