using Azure.Identity;
using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Infrastructure.Caching;
using FlightAssistant.Infrastructure.Persistance;
using FlightAssistant.Infrastructure.Persistance.Repositories;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.Identity.Web;
using Microsoft.Identity.Web.Resource;


var builder = WebApplication.CreateBuilder(args);

// Add services to the container.

builder.Services.AddControllers();
// Learn more about configuring Swagger/OpenAPI at https://aka.ms/aspnetcore/swashbuckle
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

builder.Services.AddDbContext<FlightAssistantDbContext>(opt =>
    opt.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection")));

// Caching
builder.Services.AddAppCaching(builder.Configuration);

builder.Services.AddScoped<ITravelerRepository, TravelerRepository>();


builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddMicrosoftIdentityWebApi(builder.Configuration.GetSection("Azure")); // <-- FIX

builder.Services.Configure<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme, o =>
{
    o.TokenValidationParameters.NameClaimType = "name";
});


builder.Services.AddAuthorization();

var keyVaultUri = builder.Configuration["Azure:KeyVault"];

if (!string.IsNullOrWhiteSpace(keyVaultUri))
{
    builder.Configuration.AddAzureKeyVault(new Uri(keyVaultUri), new DefaultAzureCredential());
}

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

const string scopeRequiredByApi = "access_as_user";

app.MapGet("/ping", () => "pong");

app.MapGet("/secure-test", (HttpContext http) =>
{
    http.VerifyUserHasAnyAcceptedScope(scopeRequiredByApi);
    return Results.Ok(new { ok = true, at = DateTime.UtcNow });
}).RequireAuthorization();


app.Run();
