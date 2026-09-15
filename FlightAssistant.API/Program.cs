using Azure.Core;
using Azure.Identity;
using FlightAssistant.API.Endpoints;
using FlightAssistant.API.Endpoints.Admin;
using FlightAssistant.API.HostedServices;
using FlightAssistant.API.Infrastructure;
using FlightAssistant.API.Middleware;
using FlightAssistant.Application;
using FlightAssistant.Application.Administration;
using FlightAssistant.Domain.Administration.Enums;
using FlightAssistant.Infrastructure;
using FlightAssistant.Infrastructure.Caching;
using FlightAssistant.Infrastructure.HealthChecks;
using FlightAssistant.Infrastructure.Persistance;
using Microsoft.ApplicationInsights.Extensibility;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.Identity.Web;
using Microsoft.Identity.Web.Resource;
using Microsoft.OpenApi.Models;
using Serilog;
using System.Security.Claims;
using System.Threading.RateLimiting;

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
    var kvUri = new Uri(keyVaultUri);

    // Retry up to 3 times with backoff — guards against transient 403s during
    // subscription re-activation propagation.
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
    opt.AddInterceptors(sp.GetRequiredService<AdminAuditInterceptor>());
});

// Caching
builder.Services.AddAppCaching(builder.Configuration);

// Application layer (MediatR, validators, pipeline)
builder.Services.AddApplication();

// Infrastructure layer (repositories, external services)
builder.Services.AddInfrastructure(builder.Configuration, builder.Environment.IsDevelopment());

var aiConnectionString = builder.Configuration["ApplicationInsights:ConnectionString"];
if (!string.IsNullOrWhiteSpace(aiConnectionString))
{
    builder.Services.AddApplicationInsightsTelemetry();
}
builder.Services.AddProblemDetails();
builder.Services.AddExceptionHandler<GlobalExceptionHandler>();
builder.Services.AddScoped<TravelerContext>();

var hcBuilder = builder.Services.AddHealthChecks()
    .AddCheck<DatabaseHealthCheck>("database")
    .AddCheck<AeroApiHealthCheck>("aeroapi", tags: ["external"])
    .AddCheck<ApnsHealthCheck>("apns", tags: ["external"]);

var redisConnStr = builder.Configuration.GetConnectionString("Redis");
if (!string.IsNullOrWhiteSpace(redisConnStr))
    hcBuilder.AddRedis(redisConnStr);

// Hosted services

builder.Services.AddFlightSync();

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

// Rate limiting — fixed-window per policy; limits are configurable via appsettings.json
builder.Services.AddRateLimiter(rl =>
{
    rl.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    // Flight search — public, partition by client IP. Lowered from 20 to 8 (issue #282): now that
    // AeroApiFlightSearchCachingDecorator absorbs repeated identical searches for free, this ceiling
    // only bounds the number of *distinct* searches one IP can force upstream per window, so a lower
    // number costs a legitimate user little while meaningfully cutting a scripted client's ability to
    // burn through the $5/month metered AeroAPI tier by varying the search each time.
    var fsLimit = builder.Configuration.GetValue("RateLimiting:FlightSearch:PermitLimit", 8);
    var fsWindow = builder.Configuration.GetValue("RateLimiting:FlightSearch:WindowSeconds", 60);
    rl.AddPolicy("flight-search", ctx => RateLimitPartition.GetFixedWindowLimiter(
        partitionKey: ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown",
        factory: _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = fsLimit,
            Window = TimeSpan.FromSeconds(fsWindow),
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            QueueLimit = 0
        }));

    // Loyalty programs — authenticated, partition by user ID
    var loyaltyLimit = builder.Configuration.GetValue("RateLimiting:LoyaltyPrograms:PermitLimit", 60);
    var loyaltyWindow = builder.Configuration.GetValue("RateLimiting:LoyaltyPrograms:WindowSeconds", 60);
    rl.AddPolicy("loyalty-programs", ctx => RateLimitPartition.GetFixedWindowLimiter(
        partitionKey: ctx.User.FindFirst("oid")?.Value
                      ?? ctx.User.FindFirst("sub")?.Value
                      ?? ctx.Connection.RemoteIpAddress?.ToString()
                      ?? "unknown",
        factory: _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = loyaltyLimit,
            Window = TimeSpan.FromSeconds(loyaltyWindow),
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            QueueLimit = 0
        }));

    // Admin reveal (#318) — authenticated, partition by actor, same oid/sub/IP fallback as
    // loyalty-programs above (partitioning by IP alone would throttle every admin behind one
    // office NAT together while letting a caller who simply rotates addresses dodge the limit
    // entirely). Deliberately far tighter than loyalty-programs' 60/60s: that endpoint is an
    // ordinary authenticated read; this one unmasks a value the data browser deliberately hides,
    // and exists to be reached for occasionally — a handful of support tickets in a row — not
    // browsed through. 10/60s comfortably covers that burst while making systematic enumeration
    // (walking a table's sensitive column one row at a time) require a sustained, hours-long,
    // fully-audited campaign rather than a quick script.
    var revealLimit = builder.Configuration.GetValue("RateLimiting:AdminReveal:PermitLimit", 10);
    var revealWindow = builder.Configuration.GetValue("RateLimiting:AdminReveal:WindowSeconds", 60);
    rl.AddPolicy("admin-reveal", ctx => RateLimitPartition.GetFixedWindowLimiter(
        partitionKey: ctx.User.FindFirst("oid")?.Value
                      ?? ctx.User.FindFirst("sub")?.Value
                      ?? ctx.Connection.RemoteIpAddress?.ToString()
                      ?? "unknown",
        factory: _ => new FixedWindowRateLimiterOptions
        {
            PermitLimit = revealLimit,
            Window = TimeSpan.FromSeconds(revealWindow),
            QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
            QueueLimit = 0
        }));

    // Add Retry-After header on 429
    rl.OnRejected = async (ctx, ct) =>
    {
        if (ctx.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
            ctx.HttpContext.Response.Headers.RetryAfter =
                ((int)retryAfter.TotalSeconds).ToString();
        ctx.HttpContext.Response.StatusCode = StatusCodes.Status429TooManyRequests;
        await ctx.HttpContext.Response.WriteAsync("Too many requests. Please try again later.", ct);
    };
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
if (app.Environment.IsDevelopment() || app.Environment.IsStaging())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseMiddleware<CorrelationIdMiddleware>();
app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseSerilogRequestLogging();
app.UseHttpsRedirection();
app.UseCors();
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();
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

// Admin endpoints — shared group carries auth, each file adds its own sub-prefix
var adminGroup = app.MapGroup("admin")
    .RequireAuthorization("AdminOnly")
    .RequireCors("AdminPanel");

// Populates IAdminContext from the authenticated admin's claims for the lifetime of the request —
// mirrors TravelerContextMiddleware (oid claim, falling back to sub) — so the AdminAuditInterceptor
// (Infrastructure) has an actor to stamp onto every AdminAuditLog row it writes during this
// request's SaveChanges call(s). A filter on this group only, not middleware on the whole pipeline,
// so ordinary traveler traffic never populates an actor and therefore never gets audited.
//
// CorrelationId (#319) is read off HttpContext.Items rather than recomputed: CorrelationIdMiddleware
// (which runs on every request, ahead of routing) already minted-or-accepted it and stashed it there
// under CorrelationIdMiddleware.ItemsKey — this filter just carries that same value one hop further,
// into Application-defined state Infrastructure can actually reach.
adminGroup.AddEndpointFilter(async (efiContext, next) =>
{
    var http = efiContext.HttpContext;
    var adminContext = http.RequestServices.GetRequiredService<AdminContext>();
    adminContext.ActorId = http.User.FindFirstValue("oid") ?? http.User.FindFirstValue("sub");
    adminContext.ActorName = http.User.FindFirstValue("name");
    adminContext.CorrelationId = http.Items[CorrelationIdMiddleware.ItemsKey] as string;
    return await next(efiContext);
});

adminGroup.MapAdminTravelerEndpoints();
adminGroup.MapAdminFlightEndpoints();
adminGroup.MapAdminAirlineEndpoints();
adminGroup.MapAdminAirportEndpoints();
adminGroup.MapAdminRefDataEndpoints();
adminGroup.MapAdminLoyaltyEndpoints();
adminGroup.MapAdminLoungeEndpoints();
adminGroup.MapAdminVerifyEndpoints();
adminGroup.MapAdminExportEndpoints();
adminGroup.MapAdminImportEndpoints();
adminGroup.MapAdminUsageEndpoints();
adminGroup.MapAdminHealthEndpoints();
adminGroup.MapAdminCiEndpoints();
adminGroup.MapAdminAuditEndpoints();
adminGroup.MapAdminStatsEndpoints();
adminGroup.MapAdminDataEndpoints();

// Private endpoints (ping, secure-test, whoami)
app.MapPrivateEndpoints();

// ------------------------------------------------------------
// 7. Run
// ------------------------------------------------------------
app.Run();

// Exposes Program for WebApplicationFactory in integration tests
public partial class Program { }
