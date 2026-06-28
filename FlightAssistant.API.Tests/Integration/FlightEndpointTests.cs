using System.Net;
using System.Net.Http.Json;

namespace FlightAssistant.API.Tests.Integration;

[Collection("Integration")]
public class FlightEndpointTests(FlightAssistantWebApplicationFactory factory)
{
    [Fact]
    public async Task GetTrackedFlights_ValidToken_Returns200()
    {
        var userId = Guid.NewGuid().ToString();
        var client = factory.CreateUserClient(userId);

        await client.PostAsJsonAsync("travelers", new
        {
            email = $"{userId}@example.com",
            displayName = "Test",
            countryCode = (string?)null
        });

        var response = await client.GetAsync("flights?page=1&pageSize=10");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task GetTrackedFlights_NoToken_Returns401()
    {
        var response = await factory.CreateClient().GetAsync("flights?page=1&pageSize=10");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task TrackFlight_InvalidFlightNumber_Returns400()
    {
        var client = factory.CreateUserClient();
        var tomorrow = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(1)).ToString("yyyy-MM-dd");

        var response = await client.PostAsJsonAsync("flights", new
        {
            airlineIata = "BA",
            flightNumber = "INVALID99999",   // too many digits
            serviceDate = tomorrow,
            departureAirportIata = "LHR",
            arrivalAirportIata = "JFK",
            scheduledDepartureUtc = DateTime.UtcNow.AddDays(1).AddHours(9),
            scheduledArrivalUtc  = DateTime.UtcNow.AddDays(1).AddHours(18)
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task TrackFlight_PastServiceDate_Returns400()
    {
        var client = factory.CreateUserClient();
        var yesterday = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-1)).ToString("yyyy-MM-dd");

        var response = await client.PostAsJsonAsync("flights", new
        {
            airlineIata = "BA",
            flightNumber = "BA123",
            serviceDate = yesterday,
            departureAirportIata = "LHR",
            arrivalAirportIata = "JFK",
            scheduledDepartureUtc = DateTime.UtcNow.AddDays(-1).AddHours(9),
            scheduledArrivalUtc  = DateTime.UtcNow.AddDays(-1).AddHours(18)
        });

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task SearchFlights_InvalidIataCode_Returns400()
    {
        var client = factory.CreateUserClient();
        var date = DateOnly.FromDateTime(DateTime.UtcNow).ToString("yyyy-MM-dd");

        // wrong-length IATA code should fail validation (case is normalized, not rejected)
        var response = await client.GetAsync($"flights/search?from=LH&to=JFK&date={date}");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }
}
