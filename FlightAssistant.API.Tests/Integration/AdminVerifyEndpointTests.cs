using System.Net;
using System.Net.Http.Json;
using FlightAssistant.Contracts.Requests.Admin;
using FlightAssistant.Domain.ReferenceData.Entities;
using FlightAssistant.Infrastructure.Persistance;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FlightAssistant.API.Tests.Integration;

/// <summary>
/// <c>PATCH /admin/countries/{code}/verify</c> (#331) and, as the control case that the int-keyed
/// routes are untouched, <c>PATCH /admin/airports/{id}/verify</c>. <c>Country</c> is keyed by its
/// 2-char <c>Code</c>, not an int id, so it gets its own route — <c>{code}</c> carries no route
/// constraint, and the endpoint canonicalises it with <c>ToCanonicalCode()</c> before lookup.
/// </summary>
[Collection("Integration")]
public class AdminVerifyEndpointTests(FlightAssistantWebApplicationFactory factory)
{
    private async Task SeedAsync(Action<FlightAssistantDbContext> seed)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlightAssistantDbContext>();
        seed(db);
        await db.SaveChangesAsync();
    }

    private async Task<Country?> FindCountryAsync(string code)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlightAssistantDbContext>();
        return await db.Countries.AsNoTracking().FirstOrDefaultAsync(c => c.Code == code);
    }

    // ══════════════ PATCH /admin/countries/{code}/verify ══════════════

    [Fact]
    public async Task VerifyCountry_Admin_ExistingCode_Returns204_AndPersistsVerification()
    {
        await SeedAsync(db => db.Countries.Add(Country.Create("ZY", "Test Country ZY")));

        var response = await factory.CreateAdminClient()
            .PatchAsJsonAsync("admin/countries/ZY/verify", new VerifyRequest(true));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        var country = await FindCountryAsync("ZY");
        Assert.NotNull(country);
        Assert.True(country!.IsVerified);
        Assert.NotNull(country.VerifiedBy);
    }

    [Fact]
    public async Task VerifyCountry_Admin_LowercaseCodeInUrl_Returns204_AndVerifiesTheCanonicalRow()
    {
        await SeedAsync(db => db.Countries.Add(Country.Create("ZV", "Test Country ZV")));

        var response = await factory.CreateAdminClient()
            .PatchAsJsonAsync("admin/countries/zv/verify", new VerifyRequest(true));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        var country = await FindCountryAsync("ZV");
        Assert.NotNull(country);
        Assert.True(country!.IsVerified);
    }

    [Fact]
    public async Task VerifyCountry_Admin_UnknownCode_Returns404()
    {
        var response = await factory.CreateAdminClient()
            .PatchAsJsonAsync("admin/countries/QQ/verify", new VerifyRequest(true));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task VerifyCountry_Admin_MalformedCode_Returns404_NotUnmatchedRoute()
    {
        // {code} carries no route constraint, so a non-country-shaped value like "NaN" still
        // matches the route and gets a real 404 from the handler, rather than falling through
        // to an unmatched-route response (which would fail CORS preflight, see #331).
        var response = await factory.CreateAdminClient()
            .PatchAsJsonAsync("admin/countries/NaN/verify", new VerifyRequest(true));

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task VerifyCountry_Admin_VerifiedCountry_UnverifyReturns204_AndClearsVerificationFields()
    {
        var country = Country.Create("ZW", "Test Country ZW");
        country.Verify("someone@example.com");
        await SeedAsync(db => db.Countries.Add(country));

        var response = await factory.CreateAdminClient()
            .PatchAsJsonAsync("admin/countries/ZW/verify", new VerifyRequest(false));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        var updated = await FindCountryAsync("ZW");
        Assert.NotNull(updated);
        Assert.False(updated!.IsVerified);
        Assert.Null(updated.VerifiedBy);
        Assert.Null(updated.VerifiedAt);
    }

    // ══════════════ PATCH /admin/airports/{id}/verify (unchanged int route) ══════════════

    [Fact]
    public async Task VerifyAirport_Admin_ExistingId_Returns204_AndVerifies()
    {
        var airport = Airport.Create("ZQV", null, "Verify Test Airport", "Verifyville", "GB");
        await SeedAsync(db => db.Airports.Add(airport));

        var response = await factory.CreateAdminClient()
            .PatchAsJsonAsync($"admin/airports/{airport.Id}/verify", new VerifyRequest(true));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlightAssistantDbContext>();
        var updated = await db.Airports.AsNoTracking().FirstAsync(a => a.Id == airport.Id);
        Assert.True(updated.IsVerified);
        Assert.NotNull(updated.VerifiedBy);
    }
}
