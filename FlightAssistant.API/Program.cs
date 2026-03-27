using Azure.Identity;
using FlightAssistant.API.Endpoints;
using FlightAssistant.API.HostedServices;
using FlightAssistant.Application;
using FlightAssistant.Infrastructure;
using FlightAssistant.Infrastructure.Caching;
using FlightAssistant.Infrastructure.Persistance;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.Identity.Web;
using Microsoft.Identity.Web.Resource;
using Microsoft.OpenApi.Models;
using System.Security.Claims;

// ------------------------------------------------------------
// 1. Bootstrap & Configuration
// ------------------------------------------------------------
var builder = WebApplication.CreateBuilder(args);

// Helper to enforce required configuration keys
string Cfg(string key) =>
    builder.Configuration[key] ?? throw new InvalidOperationException($"Missing configuration value: '{key}'.");

var keyVaultUri = builder.Configuration["Azure:KeyVault"];
if (!string.IsNullOrWhiteSpace(keyVaultUri))
{
    builder.Configuration.AddAzureKeyVault(new Uri(keyVaultUri), new DefaultAzureCredential());
}

// ------------------------------------------------------------
// 2. Service Registration
// ------------------------------------------------------------
builder.Services.AddControllers();
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
builder.Services.AddDbContext<FlightAssistantDbContext>(opt =>
    opt.UseNpgsql(Cfg("PostgresConnectionString")));

// Caching
builder.Services.AddAppCaching(builder.Configuration);

// Application layer (MediatR, validators, pipeline)
builder.Services.AddApplication();

// Infrastructure layer (repositories, external services)
builder.Services.AddInfrastructure(builder.Configuration);

// Hosted services

// Commented out because of slow startup in dev/test environments
builder.Services.AddHostedService<ReferenceDataSeeder>();

// ------------------------------------------------------------
// 3. Authentication & Authorization
// ------------------------------------------------------------
const string NameClaimType = "name";
const string ScopeAccessAsUser = "access_as_user";

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
            jwt.TokenValidationParameters.ValidIssuer = issuer;
            jwt.TokenValidationParameters.ValidAudience = apiClientId;
            jwt.TokenValidationParameters.NameClaimType = NameClaimType;
            jwt.IncludeErrorDetails = true;
            jwt.Events = new JwtBearerEvents
            {
                OnAuthenticationFailed = ctx =>
                {
                    Console.WriteLine("[JWT fail] " + ctx.Exception.Message);
                    return Task.CompletedTask;
                },
                OnChallenge = ctx =>
                {
                    Console.WriteLine("[JWT challenge] " + ctx.ErrorDescription);
                    return Task.CompletedTask;
                }
            };
        },
        msid =>
        {
            msid.Instance = instance;
            msid.TenantId = tenantId;
            msid.ClientId = apiClientId;
            msid.SignUpSignInPolicyId = policy;
            msid.Domain = domain;
        });

builder.Services.Configure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, o =>
{
    o.TokenValidationParameters.NameClaimType = NameClaimType;
});

builder.Services.AddAuthorization();

// ------------------------------------------------------------
// 4. Build App
// ------------------------------------------------------------
var app = builder.Build();

// ------------------------------------------------------------
// 5. Middleware Pipeline
// ------------------------------------------------------------
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();
app.UseAuthentication();
app.UseAuthorization();

// ------------------------------------------------------------
// 6. Endpoint Mapping
// ------------------------------------------------------------
app.MapControllers();

// Domain endpoints
app.MapTravelerEndpoints();
app.MapFlightEndpoints();
app.MapLoyaltyEndpoints();
app.MapLoungeEndpoints();
app.MapVisaEndpoints();

// Group: private (mixed auth)
var privateGroup = app.MapGroup("private");

// Public (no auth) ping
privateGroup.MapGet("ping", () => "pong");

// Authenticated: secure test
privateGroup.MapGet("secure-test", (HttpContext http) =>
{
    http.VerifyUserHasAnyAcceptedScope(ScopeAccessAsUser);
    return Results.Ok(new { ok = true, at = DateTime.UtcNow });
}).RequireAuthorization();

// Authenticated: whoami
privateGroup.MapGet("whoami", (HttpContext http) =>
{
    http.VerifyUserHasAnyAcceptedScope(ScopeAccessAsUser);
    return Results.Ok(new
    {
        name = http.User.Identity?.Name,
        aud = http.User.FindFirstValue("aud"),
        iss = http.User.FindFirstValue("iss"),
        tfp = http.User.FindFirst("tfp")?.Value ?? http.User.FindFirst("acr")?.Value
    });
}).RequireAuthorization();

// ------------------------------------------------------------
// 7. Run
// ------------------------------------------------------------
app.Run();
