using System.Net;
using System.Text.Json.Serialization;
using Hangfire;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.OpenApi;
using ProjectManagementWeb.Api.Security;
using ProjectManagementWeb.Application.Common;
using ProjectManagementWeb.Infrastructure;
using ProjectManagementWeb.Infrastructure.Jobs;
using ProjectManagementWeb.Infrastructure.Persistence;

namespace ProjectManagementWeb.Api;

public partial class Program
{
    public static async Task Main(string[] args)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
        ConfigureServices(builder);

        WebApplication app = builder.Build();
        if (args.Contains("--initialize-hangfire", StringComparer.Ordinal))
        {
            ConfigureReminderJobs(app);
            return;
        }

        await app.Services.EnsureBootstrapAdminAsync(app.Configuration);
        ConfigureReminderJobs(app);
        ConfigureRequestPipeline(app);

        await app.RunAsync();
    }

    private static void ConfigureServices(WebApplicationBuilder builder)
    {
        builder.Services.AddProblemDetails();
        builder.Services.AddExceptionHandler<ApiExceptionHandler>();
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddScoped<ICurrentUser, HttpCurrentUser>();
        builder.Services.AddScoped<IClientAddressProvider, HttpClientAddressProvider>();
        builder.Services.AddControllersWithViews().AddJsonOptions(options =>
            options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
        builder.Services.ConfigureHttpJsonOptions(options =>
            options.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));
        builder.Services.AddAntiforgery(options =>
        {
            options.HeaderName = "X-CSRF-TOKEN";
            options.Cookie.Name = "PMW-CSRF";
            options.Cookie.HttpOnly = true;
            options.Cookie.SameSite = SameSiteMode.Lax;
            options.Cookie.SecurePolicy = builder.Environment.IsDevelopment() ||
                                          builder.Environment.IsEnvironment("Testing")
                ? CookieSecurePolicy.SameAsRequest
                : CookieSecurePolicy.Always;
        });

        IDataProtectionBuilder dataProtection = builder.Services.AddDataProtection()
            .SetApplicationName(builder.Configuration["DataProtection:ApplicationName"] ?? "ProjectManagementWeb");
        string? keysPath = builder.Configuration["DataProtection:KeysPath"];
        if (!string.IsNullOrWhiteSpace(keysPath))
        {
            dataProtection.PersistKeysToFileSystem(new DirectoryInfo(keysPath));
        }

        builder.Services.AddInfrastructure(builder.Configuration);
        ConfigureForwardedHeaders(builder);
        ConfigureCors(builder);
        ConfigureOpenApi(builder);
        builder.Services.AddHealthChecks();
    }

    private static void ConfigureCors(WebApplicationBuilder builder)
    {
        string[] origins = builder.Configuration.GetSection("Cors:AllowedOrigins").Get<string[]>()
            ?? ["http://localhost:5173"];
        builder.Services.AddCors(options => options.AddPolicy("VueSpa", policy =>
            policy.WithOrigins(origins).AllowAnyHeader().AllowAnyMethod().AllowCredentials()));
    }

    private static void ConfigureForwardedHeaders(WebApplicationBuilder builder)
    {
        string[] configuredProxies = builder.Configuration.GetSection("ReverseProxy:KnownProxies").Get<string[]>() ?? [];
        string[] configuredNetworks = builder.Configuration.GetSection("ReverseProxy:KnownNetworks").Get<string[]>() ?? [];
        var proxies = new List<IPAddress>();
        var networks = new List<System.Net.IPNetwork>();

        foreach (string configuredProxy in configuredProxies)
        {
            if (!IPAddress.TryParse(configuredProxy, out IPAddress? proxy))
            {
                throw new InvalidOperationException($"ReverseProxy:KnownProxies 包含無效 IP：{configuredProxy}");
            }
            proxies.Add(proxy);
        }

        foreach (string configuredNetwork in configuredNetworks)
        {
            if (!System.Net.IPNetwork.TryParse(configuredNetwork, out System.Net.IPNetwork network))
            {
                throw new InvalidOperationException($"ReverseProxy:KnownNetworks 包含無效 CIDR：{configuredNetwork}");
            }
            networks.Add(network);
        }

        builder.Services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            options.ForwardLimit = 1;
            options.KnownProxies.Clear();
            options.KnownIPNetworks.Clear();
            foreach (IPAddress proxy in proxies)
            {
                options.KnownProxies.Add(proxy);
            }
            foreach (System.Net.IPNetwork network in networks)
            {
                options.KnownIPNetworks.Add(network);
            }
        });
    }

    private static void ConfigureOpenApi(WebApplicationBuilder builder)
    {
        builder.Services.AddOpenApi("v1", options => options.AddDocumentTransformer((document, _, _) =>
        {
            document.Components ??= new OpenApiComponents();
            document.Components.SecuritySchemes ??= new Dictionary<string, IOpenApiSecurityScheme>();
            document.Components.SecuritySchemes["Bearer"] = CreateBearerSecurityScheme();
            return Task.CompletedTask;
        }));
        builder.Services.AddSwaggerGen(options =>
        {
            options.SwaggerDoc("v1", new() { Title = "ProjectManagementWeb API", Version = "v1" });
            options.AddSecurityDefinition("Bearer", CreateBearerSecurityScheme());
            options.AddSecurityRequirement(document => new OpenApiSecurityRequirement
            {
                [new OpenApiSecuritySchemeReference("Bearer", document)] = []
            });
        });
    }

    private static OpenApiSecurityScheme CreateBearerSecurityScheme() => new()
    {
        Type = SecuritySchemeType.Http,
        Scheme = JwtBearerDefaults.AuthenticationScheme,
        BearerFormat = "JWT",
        Description = "輸入 JWT Access Token。"
    };

    private static void ConfigureRequestPipeline(WebApplication app)
    {
        if (app.Configuration.GetSection("ReverseProxy:KnownProxies").GetChildren().Any() ||
            app.Configuration.GetSection("ReverseProxy:KnownNetworks").GetChildren().Any())
        {
            app.UseForwardedHeaders();
        }
        app.UseExceptionHandler();
        app.UseCors("VueSpa");

        bool exposeOpenApi = app.Environment.IsDevelopment() || app.Environment.IsEnvironment("Testing") ||
                             app.Configuration.GetValue<bool>("OpenApi:Enabled");
        if (exposeOpenApi)
        {
            app.MapOpenApi("/openapi/{documentName}.json");
            app.UseSwagger();
            app.UseSwaggerUI(options =>
                options.SwaggerEndpoint("/swagger/v1/swagger.json", "ProjectManagementWeb API v1"));
        }

        app.UseAuthentication();
        app.UseAuthorization();
        app.MapHealthChecks("/health");
        app.MapControllers();
    }

    private static void ConfigureReminderJobs(WebApplication app)
    {
        if (!app.Configuration.GetValue("ReminderJobs:Enabled", true))
        {
            return;
        }

        IRecurringJobManager jobs = app.Services.GetRequiredService<IRecurringJobManager>();
        jobs.AddOrUpdate<ReminderJobs>(
            "task-reminder-scan",
            job => job.ScanAsync(CancellationToken.None),
            Cron.Minutely);
        jobs.AddOrUpdate<ReminderJobs>(
            "task-reminder-send",
            job => job.SendAsync(CancellationToken.None),
            Cron.Minutely);
    }
}
