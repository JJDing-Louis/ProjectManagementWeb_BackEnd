using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.OpenApi;
using ProjectManagementWeb.Api.Security;
using ProjectManagementWeb.Application.Common;
using ProjectManagementWeb.Infrastructure;
using ProjectManagementWeb.Infrastructure.Persistence;

namespace ProjectManagementWeb.Api;

public partial class Program
{
    public static async Task Main(string[] args)
    {
        WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
        ConfigureServices(builder);

        WebApplication app = builder.Build();
        await app.Services.EnsureBootstrapAdminAsync(app.Configuration);
        ConfigureRequestPipeline(app);

        await app.RunAsync();
    }

    private static void ConfigureServices(WebApplicationBuilder builder)
    {
        builder.Services.AddProblemDetails();
        builder.Services.AddExceptionHandler<ApiExceptionHandler>();
        builder.Services.AddHttpContextAccessor();
        builder.Services.AddScoped<ICurrentUser, HttpCurrentUser>();
        builder.Services.AddControllersWithViews().AddJsonOptions(options =>
            options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter()));
        builder.Services.AddAntiforgery(options =>
        {
            options.HeaderName = "X-CSRF-TOKEN";
            options.Cookie.Name = "PMW-CSRF";
            options.Cookie.HttpOnly = true;
            options.Cookie.SameSite = SameSiteMode.Lax;
            options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
        });

        IDataProtectionBuilder dataProtection = builder.Services.AddDataProtection()
            .SetApplicationName(builder.Configuration["DataProtection:ApplicationName"] ?? "ProjectManagementWeb");
        string? keysPath = builder.Configuration["DataProtection:KeysPath"];
        if (!string.IsNullOrWhiteSpace(keysPath))
        {
            dataProtection.PersistKeysToFileSystem(new DirectoryInfo(keysPath));
        }

        builder.Services.AddInfrastructure(builder.Configuration);
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
}
