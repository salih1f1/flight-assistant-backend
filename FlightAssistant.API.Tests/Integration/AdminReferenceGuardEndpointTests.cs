using System.Net;
using System.Net.Http.Json;

namespace FlightAssistant.API.Tests.Integration;

/// <summary>
/// The reference-existence guards (#455) end to end, for the entities whose foreign key is not an
/// alliance — airports, lounges and loyalty programmes. Like <see cref="AdminAirlineEndpointTests"/>,
/// these could never have gone red on the original defect: this suite runs on the InMemory
/// provider, which enforces no foreign key at all, so an unknown id or code used to save happily
/// here while producing a 500 against PostgreSQL. What they prove is that each guard is wired
/// through DI — every one of these handlers gained a constructor dependency — and that the route
/// answers 409 rather than 500.
/// </summary>
[Collection("Integration")]
public class AdminReferenceGuardEndpointTests(FlightAssistantWebApplicationFactory factory)
{
    [Fact]
    public async Task CreateAirport_CountryCodeThatIsNoCountry_Returns409AsAProblemDocument()
    {
        var client = factory.CreateAdminClient();

        var response = await client.PostAsJsonAsync("admin/airports", new
        {
            iata = "QZZ",
            icao = "QZZA",
            name = "Nowhere International",
            city = "Nowhere",
            countryCode = "ZZ",
        });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Contains("ZZ", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task CreateLounge_AirportIdThatIsNoAirport_Returns409()
    {
        var client = factory.CreateAdminClient();

        var response = await client.PostAsJsonAsync("admin/lounges", new
        {
            airportId = 987654,
            name = "Ghost Lounge",
            isAirside = true,
            isDomesticOnly = false,
            isInternationalOnly = false,
        });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("987654", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task CreateLoyaltyProgram_KindIdThatIsNoKind_Returns409()
    {
        var client = factory.CreateAdminClient();

        var response = await client.PostAsJsonAsync("admin/loyalty/programs", new
        {
            code = "GHOST",
            name = "Ghost Club",
            kindId = 987654,
        });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("987654", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task CreateLoyaltyProgram_AirlineIdThatIsNoAirline_Returns409()
    {
        var client = factory.CreateAdminClient();

        var response = await client.PostAsJsonAsync("admin/loyalty/programs", new
        {
            code = "GHOST2",
            name = "Ghost Club Two",
            kindId = 1,
            airlineId = 987654,
        });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }
}
