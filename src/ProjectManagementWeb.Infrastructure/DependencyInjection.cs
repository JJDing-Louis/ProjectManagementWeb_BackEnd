using Hangfire;
using Hangfire.SqlServer;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ProjectManagementWeb.Application.Auth;
using ProjectManagementWeb.Application.Comments;
using ProjectManagementWeb.Application.Common;
using ProjectManagementWeb.Application.Preferences;
using ProjectManagementWeb.Application.Projects;
using ProjectManagementWeb.Application.Reminders;
using ProjectManagementWeb.Application.Tasks;
using ProjectManagementWeb.Application.Users;
using ProjectManagementWeb.Infrastructure.Configuration;
using ProjectManagementWeb.Infrastructure.Email;
using ProjectManagementWeb.Infrastructure.Identity;
using ProjectManagementWeb.Infrastructure.Jobs;
using ProjectManagementWeb.Infrastructure.Persistence;
using ProjectManagementWeb.Infrastructure.Security;
using ProjectManagementWeb.Infrastructure.Services;

namespace ProjectManagementWeb.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        string connectionString = configuration.GetConnectionString("DefaultConnection")
            ?? throw new InvalidOperationException("缺少 ConnectionStrings:DefaultConnection 設定。");

        services.Configure<JwtOptions>(configuration.GetSection(JwtOptions.SectionName));
        services.Configure<SmtpOptions>(configuration.GetSection(SmtpOptions.SectionName));
        services.AddDbContextFactory<ApplicationDbContext>(options => options.UseSqlServer(connectionString));
        services.AddIdentityCore<ApplicationUser>(options =>
            {
                options.User.RequireUniqueEmail = true;
                options.User.AllowedUserNameCharacters = RegistrationRules.AllowedAccountCharacters;
                options.SignIn.RequireConfirmedEmail = false;
                options.Password.RequiredLength = RegistrationRules.PasswordMinLength;
                options.Password.RequireDigit = true;
                options.Password.RequireLowercase = true;
                options.Password.RequireUppercase = true;
                options.Password.RequireNonAlphanumeric = true;
            })
            .AddRoles<ApplicationRole>()
            .AddEntityFrameworkStores<ApplicationDbContext>()
            .AddSignInManager()
            .AddDefaultTokenProviders();

        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(new BootstrapAdminPolicy(configuration["BootstrapAdmin:Account"]));
        services.AddSingleton<JwtTokenIssuer>();
        services.AddSingleton<ITokenIssuer>(provider => provider.GetRequiredService<JwtTokenIssuer>());
        services.AddSingleton<IConfigureOptions<JwtBearerOptions>, JwtBearerOptionsSetup>();
        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
        services.AddAuthorization();

        services.AddScoped<IEmailGateway, SmtpEmailGateway>();
        services.AddScoped<ServiceSupport>();
        services.AddScoped<IBusinessCodeGenerator, BusinessCodeGenerator>();
        services.AddScoped<IAuthService, AuthService>();
        services.AddScoped<IUserService, UserService>();
        services.AddScoped<IPreferenceService, PreferenceService>();
        services.AddScoped<IProjectService, ProjectService>();
        services.AddScoped<ITaskService, TaskService>();
        services.AddScoped<ICommentService, CommentService>();
        services.AddScoped<IReminderService, ReminderService>();
        services.AddScoped<ReminderJobs>();
        if (configuration.GetValue("ReminderJobs:Enabled", true))
        {
            services.AddHangfire(options => options
                .SetDataCompatibilityLevel(CompatibilityLevel.Version_180)
                .UseSimpleAssemblyNameTypeSerializer()
                .UseRecommendedSerializerSettings()
                .UseSqlServerStorage(connectionString, new SqlServerStorageOptions
                {
                    PrepareSchemaIfNecessary = configuration.GetValue(
                        "Hangfire:PrepareSchemaIfNecessary",
                        false),
                    QueuePollInterval = TimeSpan.FromSeconds(15),
                    UseRecommendedIsolationLevel = true,
                    DisableGlobalLocks = true
                }));
            services.AddHangfireServer(options => options.WorkerCount = 2);
        }
        return services;
    }
}
