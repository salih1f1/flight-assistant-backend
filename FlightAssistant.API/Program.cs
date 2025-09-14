using Azure.Identity;
using FlightAssistant.API.HostedServices;
using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Infrastructure.Caching;
using FlightAssistant.Infrastructure.Persistance;
using FlightAssistant.Infrastructure.Persistance.Repositories;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.Identity.Web;
using Microsoft.Identity.Web.Resource;
using Microsoft.OpenApi.Models;

var builder = WebApplication.CreateBuilder(args);

var keyVaultUri = builder.Configuration["Azure:KeyVault"];

if (!string.IsNullOrWhiteSpace(keyVaultUri))
{
    builder.Configuration.AddAzureKeyVault(new Uri(keyVaultUri), new DefaultAzureCredential());
}

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
        Reference = new OpenApiReference
        {
            Type = ReferenceType.SecurityScheme,
            Id = "Bearer"
        }
    };
    c.AddSecurityDefinition("Bearer", securityScheme);
    c.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        { securityScheme, new List<string>() }
    });
});

builder.Services.AddDbContext<FlightAssistantDbContext>(opt =>
    opt.UseNpgsql(builder.Configuration["PostgresConnectionString-Local"]));

// Caching
builder.Services.AddAppCaching(builder.Configuration);
builder.Services.AddScoped<ITravelerRepository, TravelerRepository>();

var tenantId = builder.Configuration["Azure:TenantId"];
var apiClientId = builder.Configuration["Azure:ClientId"];
var instance = builder.Configuration["Azure:Instance"];
var domain = builder.Configuration["Azure:Domain"];
var policy = builder.Configuration["Azure:SignUpSignInPolicyId"];
var issuer = String.Format(builder.Configuration["Azure:Issuer"], tenantId);
var authority = String.Format(builder.Configuration["Azure:Authority"], tenantId);

const string nameClaimType = "name";
const string scopeRequiredByApi = "access_as_user";

builder.Services
  .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddMicrosoftIdentityWebApi(
        jwt =>
        {
            jwt.Authority = authority;
            jwt.TokenValidationParameters.ValidIssuer = issuer;
            jwt.TokenValidationParameters.ValidAudience = apiClientId;
            jwt.TokenValidationParameters.NameClaimType = nameClaimType;
            jwt.IncludeErrorDetails = true;
            jwt.Events = new JwtBearerEvents
            {
                OnAuthenticationFailed = c => { Console.WriteLine("[JWT fail] " + c.Exception.Message); return Task.CompletedTask; },
                OnChallenge = c => { Console.WriteLine("[JWT challenge] " + c.ErrorDescription); return Task.CompletedTask; }
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
    o.TokenValidationParameters.NameClaimType = nameClaimType;
});

builder.Services.AddAuthorization();

builder.Services.AddHostedService<ReferenceDataSeeder>();

var app = builder.Build();

app.UseAuthentication();
app.UseAuthorization();

// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();

app.UseAuthorization();

app.MapControllers();

app.MapGet("/ping", () => "pong");

app.MapGet("/secure-test", (HttpContext http) =>
{
    http.VerifyUserHasAnyAcceptedScope(scopeRequiredByApi);
    return Results.Ok(new { ok = true, at = DateTime.UtcNow });
}).RequireAuthorization();

app.MapGet("/whoami", (HttpContext http) =>
{
    http.VerifyUserHasAnyAcceptedScope();
    return Results.Ok(new
    {
        name = http.User.Identity?.Name,
        aud = http.User.FindFirst("aud")?.Value,
        scp = http.User.FindFirst("scp")?.Value,
        iss = http.User.FindFirst("iss")?.Value,
        tfp = http.User.FindFirst("tfp")?.Value ?? http.User.FindFirst("acr")?.Value
    });
}).RequireAuthorization();

app.Run();
