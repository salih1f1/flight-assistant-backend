using System.Net;
using System.Net.Http.Json;
using FlightAssistant.Application.Interfaces.ExternalServices;
using FlightAssistant.Contracts.Requests.Flights;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

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
    public async Task TrackFlight_AlreadyLanded_Returns400()
    {
        // Scheduled arrival 6 hours ago: a flight is in the past once it has landed, not once its
        // service date has (#350).
        var client = factory.CreateUserClient();
        var yesterday = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-1)).ToString("yyyy-MM-dd");

        var response = await client.PostAsJsonAsync("flights", new
        {
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

    [Fact]
    public async Task SearchFlights_ReturnedRow_DeserialisesIntoAValidTrackFlightRequest()
    {
        // This is the whole point of task 4's reshape: GET /flights/search's response shape must
        // be postable straight to POST /flights with no client-side transformation. Prove it at
        // the real HTTP/JSON boundary — through the endpoint's actual mapping code, not a
        // hand-rolled duplicate of it — by substituting the search provider with a fake and
        // deserialising the raw response body into TrackFlightRequest.
        // Relative to today, never a pinned literal: SearchFlightsQueryValidator bounds the date to
        // the provider's rolling past/future horizon, so a hardcoded date eventually falls out of it
        // and the test starts failing for a reason that has nothing to do with the response shape
        // it exists to check.
        var serviceDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(30));
        var fakeResult = new FlightSearchResult(
            FlightNumber: "6E4120",
            ServiceDate: serviceDate,
            DepartureAirportIata: "SOF",
            ArrivalAirportIata: "IST",
            ScheduledDepartureUtc: serviceDate.ToDateTime(new TimeOnly(6, 35), DateTimeKind.Utc),
            ScheduledArrivalUtc: serviceDate.ToDateTime(new TimeOnly(8, 5), DateTimeKind.Utc));

        var fakeProvider = Substitute.For<IFlightSearchProvider>();
        fakeProvider.SearchAsync(Arg.Any<FlightSearchCriteria>(), Arg.Any<CancellationToken>())
            .Returns([fakeResult]);

        using var scopedFactory = factory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
                services.AddScoped<IFlightSearchProvider>(_ => fakeProvider)));
        // GET /flights/search is AllowAnonymous — no auth header needed.
        var client = scopedFactory.CreateClient();

        var response = await client.GetAsync(
            $"flights/search?flightNumber=6E4120&date={serviceDate:yyyy-MM-dd}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var rows = await response.Content.ReadFromJsonAsync<List<TrackFlightRequest>>();
        var row = Assert.Single(rows!);

        Assert.Equal(fakeResult.FlightNumber, row.FlightNumber);
        Assert.Equal(fakeResult.ServiceDate, row.ServiceDate);
        Assert.Equal(fakeResult.DepartureAirportIata, row.DepartureAirportIata);
        Assert.Equal(fakeResult.ArrivalAirportIata, row.ArrivalAirportIata);
        Assert.Equal(fakeResult.ScheduledDepartureUtc, row.ScheduledDepartureUtc);
        Assert.Equal(fakeResult.ScheduledArrivalUtc, row.ScheduledArrivalUtc);
    }
}
