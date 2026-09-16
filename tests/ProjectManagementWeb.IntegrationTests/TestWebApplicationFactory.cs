using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using ProjectManagementWeb.Api;
using ProjectManagementWeb.Application.Common;
using ProjectManagementWeb.Application.Users;
using ProjectManagementWeb.Infrastructure.Email;
using ProjectManagementWeb.Infrastructure.Persistence;

namespace ProjectManagementWeb.IntegrationTests;

internal sealed class TestWebApplicationFactory : WebApplicationFactory<Program>
{
    private readonly string _connectionString;
    private readonly string? _bootstrapAdminAccount;
    private readonly SaveChangesInterceptor? _saveChangesInterceptor;
    private readonly string? _jwtPrivateKeyPem;
    private readonly TimeProvider? _timeProvider;
    private readonly bool _replaceClientAddressProvider;
    private readonly string? _knownProxy;
    private readonly string? _knownNetwork;
    private readonly bool _enableReminderJobs;

    public TestWebApplicationFactory(
        string connectionString,
        string? bootstrapAdminAccount = null,
        SaveChangesInterceptor? saveChangesInterceptor = null,
        string? jwtPrivateKeyPem = null,
        TimeProvider? timeProvider = null,
        bool replaceClientAddressProvider = true,
        string? knownProxy = null,
        string? knownNetwork = null,
        bool enableReminderJobs = false)
    {
        _connectionString = connectionString;
        _bootstrapAdminAccount = bootstrapAdminAccount;
        _saveChangesInterceptor = saveChangesInterceptor;
        _jwtPrivateKeyPem = jwtPrivateKeyPem;
        _timeProvider = timeProvider;
        _replaceClientAddressProvider = replaceClientAddressProvider;
        _knownProxy = knownProxy;
        _knownNetwork = knownNetwork;
        _enableReminderJobs = enableReminderJobs;
    }

    public TestEmailGateway EmailGateway { get; } = new();
    public TestLogSink LogSink { get; } = new();
    public TestClientAddressProvider ClientAddressProvider { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Testing");
        builder.UseSetting("ConnectionStrings:DefaultConnection", _connectionString);
        builder.UseSetting("ReminderJobs:Enabled", _enableReminderJobs ? "true" : "false");
        builder.UseSetting("Hangfire:PrepareSchemaIfNecessary", _enableReminderJobs ? "true" : "false");
        if (_jwtPrivateKeyPem is not null) builder.UseSetting("Jwt:PrivateKeyPem", _jwtPrivateKeyPem);
        if (_knownProxy is not null) builder.UseSetting("ReverseProxy:KnownProxies:0", _knownProxy);
        if (_knownNetwork is not null) builder.UseSetting("ReverseProxy:KnownNetworks:0", _knownNetwork);
        builder.ConfigureLogging(logging => logging.AddProvider(LogSink));
        builder.ConfigureTestServices(services =>
        {
            services.RemoveAll<IEmailGateway>();
            services.AddSingleton(EmailGateway);
            services.AddSingleton<IEmailGateway>(provider => provider.GetRequiredService<TestEmailGateway>());
            if (_replaceClientAddressProvider)
            {
                services.RemoveAll<IClientAddressProvider>();
                services.AddSingleton<IClientAddressProvider>(ClientAddressProvider);
            }
            if (_timeProvider is not null)
            {
                services.RemoveAll<TimeProvider>();
                services.AddSingleton(_timeProvider);
            }
            if (_saveChangesInterceptor is not null)
            {
                services.AddSingleton<SaveChangesInterceptor>(_saveChangesInterceptor);
                services.RemoveAll<IDbContextFactory<ApplicationDbContext>>();
                services.AddDbContextFactory<ApplicationDbContext>((provider, options) =>
                    options.UseSqlServer(_connectionString)
                        .AddInterceptors(provider.GetRequiredService<SaveChangesInterceptor>()));
                services.AddDbContext<ApplicationDbContext>((provider, options) =>
                    options.AddInterceptors(provider.GetRequiredService<SaveChangesInterceptor>()));
            }
            if (!string.IsNullOrWhiteSpace(_bootstrapAdminAccount))
            {
                services.RemoveAll<BootstrapAdminPolicy>();
                services.AddSingleton(new BootstrapAdminPolicy(_bootstrapAdminAccount));
            }
        });
    }
}
