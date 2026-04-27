using Azure.Core;
using Azure.Identity;
using FlightAssistant.API.Endpoints;
using FlightAssistant.API.Endpoints.Admin;
using FlightAssistant.API.HostedServices;
using FlightAssistant.API.Infrastructure;
using FlightAssistant.API.Middleware;
using FlightAssistant.Application;
using FlightAssistant.Infrastructure;
using FlightAssistant.Infrastructure.Caching;
using FlightAssistant.Infrastructure.HealthChecks;
using FlightAssistant.Infrastructure.Persistance;
using Microsoft.ApplicationInsights.Extensibility;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.Identity.Web;
using Microsoft.Identity.Web.Resource;
using Microsoft.OpenApi.Models;
using Serilog;

// ------------------------------------------------------------
// 1. Bootstrap & Configuration
// ------------------------------------------------------------
var builder = WebApplication.CreateBuilder(args);

// Serilog bootstrap logger — captures startup errors before full config loads
Log.Logger = new LoggerConfiguration()
    .WriteTo.Console()
    .CreateBootstrapLogger();

builder.Host.UseSerilog((ctx, svc, logCfg) =>
{
    logCfg
        .ReadFrom.Configuration(ctx.Configuration)
        .ReadFrom.Services(svc)
        .Enrich.FromLogContext()
        .Enrich.WithEnvironmentName()
        .Enrich.WithMachineName();

    if (ctx.HostingEnvironment.IsDevelopment())
        logCfg.WriteTo.Console();

    var aiConnectionString = ctx.Configuration["ApplicationInsights:ConnectionString"];
    if (!string.IsNullOrWhiteSpace(aiConnectionString))
    {
        logCfg.WriteTo.ApplicationInsights(
            new TelemetryConfiguration { ConnectionString = aiConnectionString },
            TelemetryConverter.Traces);
    }
});

// Helper to enforce required configuration keys
string Cfg(string key) =>
    builder.Configuration[key] ?? throw new InvalidOperationException($"Missing configuration value: '{key}'.");

var keyVaultUri = builder.Configuration["Azure:KeyVault"];
if (!string.IsNullOrWhiteSpace(keyVaultUri))
{
    TokenCredential kvCredential = new DefaultAzureCredential();

    // Retry up to 3 times with backoff — guards against transient 403s during
    // subscription re-activation propagation.
    var kvUri = new Uri(keyVaultUri);
    for (int attempt = 1; ; attempt++)
    {
        try
        {
            builder.Configuration.AddAzureKeyVault(kvUri, kvCredential);
            break;
        }
        catch (Exception) when (attempt < 3)
        {
            Thread.Sleep(TimeSpan.FromSeconds(attempt * 5));
        }
    }
}

// ------------------------------------------------------------
// 2. Service Registration
// ------------------------------------------------------------
builder.Services.AddEndpointsApiExplorer();

builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo { Title = "FlightAssistant API", Version = "v1" });
    var securityScheme = new OpenApiSecurityScheme
    {
        Name = "Authorization",
        Description = "Enter 'Bearer' followed by a space and the JWT.",
        In = ParameterLocation.Header,
        Type = SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT",
        Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "Bearer" }
    };
    c.AddSecurityDefinition("Bearer", securityScheme);
    c.AddSecurityRequirement(new OpenApiSecurityRequirement { { securityScheme, new List<string>() } });
});

// Database
builder.Services.AddDbContext<FlightAssistantDbContext>((sp, opt) =>
{
    opt.UseNpgsql(Cfg("PostgresConnectionString"));
    opt.AddInterceptors(sp.GetRequiredService<DomainEventDispatchInterceptor>());
});

// Caching
builder.Services.AddAppCaching(builder.Configuration);

// Application layer (MediatR, validators, pipeline)
builder.Services.AddApplication();

// Infrastructure layer (repositories, external services)
builder.Services.AddInfrastructure(builder.Configuration, builder.Environment.IsDevelopment());

builder.Services.AddApplicationInsightsTelemetry();
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
builder.Services.AddScoped<TravelerContext>();

builder.Services.AddHealthChecks()
    .AddCheck<DatabaseHealthCheck>("database")
    .AddRedis(builder.Configuration.GetConnectionString("Redis") ?? string.Empty)
    .AddCheck<AeroDataBoxHealthCheck>("aerodatabox", tags: ["external"])
    .AddCheck<ApnsHealthCheck>("apns", tags: ["external"]);

// Hosted services

// Commented out because of slow startup in dev/test environments
builder.Services.AddHostedService<ReferenceDataSeeder>();
builder.Services.AddHostedService<FlightStatusSyncBackgroundService>();

// ------------------------------------------------------------
// 3. Authentication & Authorization
// ------------------------------------------------------------
const string NameClaimType = "name";

// Azure AD B2C configuration (kept explicit for clarity)
var tenantId = Cfg("Azure:TenantId");
var apiClientId = Cfg("Azure:ClientId");
var instance = Cfg("Azure:Instance");
var domain = Cfg("Azure:Domain");
var policy = Cfg("Azure:SignUpSignInPolicyId");

// Issuer / Authority may contain format placeholders requiring tenant insertion
var issuer = string.Format(Cfg("Azure:Issuer"), tenantId);
var authority = string.Format(Cfg("Azure:Authority"), tenantId);

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddMicrosoftIdentityWebApi(
        jwt =>
        {
            jwt.Authority = authority;
            jwt.MapInboundClaims = false;
            jwt.TokenValidationParameters.ValidIssuer = issuer;
            jwt.TokenValidationParameters.ValidAudience = apiClientId;
            jwt.TokenValidationParameters.NameClaimType = NameClaimType;
            jwt.TokenValidationParameters.RoleClaimType = "roles";
            jwt.IncludeErrorDetails = builder.Environment.IsDevelopment();
        },
        msid =>
        {
            msid.Instance = instance;
            msid.TenantId = tenantId;
            msid.ClientId = apiClientId;
            msid.SignUpSignInPolicyId = policy;
            msid.Domain = domain;
        });


builder.Services.AddCors(options =>
{
    options.AddPolicy("AdminPanel", policy =>
        policy.WithOrigins(
                builder.Configuration["AdminPanel:AllowedOrigin"] ?? "http://localhost:5173"
            )
            .AllowAnyHeader()
            .AllowAnyMethod());
});

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("AdminOnly", policy =>
        policy.RequireAuthenticatedUser()
              .RequireRole("admin"));
});

// ------------------------------------------------------------
// 4. Build App
// ------------------------------------------------------------
var app = builder.Build();

// Wire up JWT event logging now that the DI container is built
var jwtLogger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Auth.Jwt");
app.Services.GetRequiredService<IOptionsMonitor<JwtBearerOptions>>()
    .Get(JwtBearerDefaults.AuthenticationScheme).Events = new JwtBearerEvents
    {
        OnAuthenticationFailed = ctx =>
        {
            jwtLogger.LogWarning("JWT authentication failed: {Message}", ctx.Exception.Message);
            return Task.CompletedTask;
        },
        OnChallenge = ctx =>
        {
            jwtLogger.LogWarning("JWT challenge: {Description}", ctx.ErrorDescription);
            return Task.CompletedTask;
        }
    };

// ------------------------------------------------------------
// 5. Middleware Pipeline
// ------------------------------------------------------------
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseMiddleware<CorrelationIdMiddleware>();
app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseSerilogRequestLogging();
app.UseHttpsRedirection();
app.UseCors("AdminPanel");
app.UseAuthentication();
app.UseAuthorization();
app.UseMiddleware<TravelerContextMiddleware>();

// ------------------------------------------------------------
// 6. Endpoint Mapping
// ------------------------------------------------------------
app.MapHealthEndpoints();

// Domain endpoints
app.MapTravelerEndpoints();
app.MapFlightEndpoints();
app.MapLoyaltyEndpoints();
app.MapLoungeEndpoints();
app.MapVisaEndpoints();

// Admin endpoints — shared group carries auth, each file adds its own sub-prefix
var adminGroup = app.MapGroup("admin").RequireAuthorization("AdminOnly");
adminGroup.MapAdminTravelerEndpoints();
adminGroup.MapAdminFlightEndpoints();
adminGroup.MapAdminAirlineEndpoints();
adminGroup.MapAdminAirportEndpoints();
adminGroup.MapAdminRefDataEndpoints();
adminGroup.MapAdminLoyaltyEndpoints();
adminGroup.MapAdminLoungeEndpoints();

// Private endpoints (ping, secure-test, whoami)
app.MapPrivateEndpoints();

// ------------------------------------------------------------
// 7. Run
// ------------------------------------------------------------
app.Run();
