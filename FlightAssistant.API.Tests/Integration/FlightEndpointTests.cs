using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FlightAssistant.Application.FlightSync;
using FlightAssistant.Application.Interfaces.ExternalServices;
using FlightAssistant.Application.Interfaces.Verification;
using FlightAssistant.Contracts.Requests.Flights;
using FlightAssistant.Domain.Common;
using FlightAssistant.Domain.Flights.Entities;
using FlightAssistant.Domain.Flights.Enums;
using FlightAssistant.Domain.ReferenceData.Entities;
using FlightAssistant.Domain.Verification.Enums;
using FlightAssistant.Infrastructure.Persistance;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

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
    public async Task GetTrackedFlights_CarriesSlotUsageAndTheCap()
    {
        // Track one flight, so the expected count is 1 rather than 0 (#397). Asserting 0 on a
        // traveler that tracks nothing cannot distinguish a computed count from a hardcoded one —
        // proven: hardcoding 0 in the endpoint left the previous version of this test green.
        // The helper seeds its own reference rows and dates the flight 30 days out, so the flight
        // is still active; one POST stays inside the Testing profile's 2 tracking permits.
        var (client, body) = await TravelerReadyToTrackAsync();

        // Fail loudly here rather than silently asserting a count further down.
        Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync("flights", body)).StatusCode);

        var page = await client.GetFromJsonAsync<JsonElement>("flights?page=1&pageSize=10");

        var cap = factory.Services.GetRequiredService<IFlightSyncOptions>().MaxActiveTrackedFlightsPerTraveler;
        Assert.Equal(1, page.GetProperty("activeTrackedFlights").GetInt32());
        Assert.Equal(cap, page.GetProperty("trackingCap").GetInt32());
        // Additive: the paging members a client already reads are untouched.
        Assert.Equal(1, page.GetProperty("total").GetInt32());
        Assert.Equal(1, page.GetProperty("page").GetInt32());
        // The flight is 30 days out, so it still occupies a slot.
        Assert.True(page.GetProperty("items")[0].GetProperty("countsTowardCap").GetBoolean());
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

    private int FlightSyncPermitLimit() =>
        factory.Services.GetRequiredService<IConfiguration>().GetValue("RateLimiting:FlightSync:PermitLimit", 6);

    /// <summary>A registered traveler tracking a flight 30 days out: Schedule mode, so a sync makes
    /// no live-status call — since #444 it does run the daily schedule check, which the factory
    /// answers with no rows. A fresh traveler id each time, so tests never share a rate-limit
    /// bucket.</summary>
    private async Task<(HttpClient Client, Flight Flight)> TravelerTrackingAFlightDaysAwayAsync()
    {
        var userId = Guid.NewGuid();
        var client = factory.CreateUserClient(userId.ToString());
        await client.PostAsJsonAsync("travelers", new { email = $"{userId}@example.com", displayName = "Test", countryCode = (string?)null });

        var departure = DateTime.UtcNow.Date.AddDays(30).AddHours(9);
        var flight = Flight.Create(1, $"TK{Random.Shared.Next(1000, 9999)}", 10, 20, DateOnly.FromDateTime(departure), departure, departure.AddHours(3));
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlightAssistantDbContext>();
        db.Flights.Add(flight);
        await db.SaveChangesAsync();
        db.TravelerFlights.Add(TravelerFlight.Create(userId, flight.Id));
        await db.SaveChangesAsync();
        return (client, flight);
    }

    [Fact]
    public async Task PostSync_TrackingTraveler_FlightDaysAway_Returns200WithUpdatedFalse_AndPlansTheFlight()
    {
        var (client, flight) = await TravelerTrackingAFlightDaysAwayAsync();

        var response = await client.PostAsync($"flights/{flight.Id}/sync", content: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("updated", Assert.Single(body.EnumerateObject()).Name);
        Assert.Equal(JsonValueKind.False, body.GetProperty("updated").ValueKind);

        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FlightAssistantDbContext>();
            var saved = await db.Flights.AsNoTracking().SingleAsync(f => f.Id == flight.Id);
            Assert.Equal(FlightSyncMode.Schedule, saved.SyncMode);
            // AeroApi:StatusWindowFutureDays defaults to 2: proves the host's IFlightStatusWindowOptions reaches the handler.
            Assert.Equal(flight.ScheduledDepartureUtc.AddDays(-2), saved.NextSyncDueUtc);
        }
    }

    [Fact]
    public async Task PostSync_RateLimit_Returns429WithRetryAfter_AfterTheConfiguredPermitCount()
    {
        var (client, flight) = await TravelerTrackingAFlightDaysAwayAsync();

        for (var i = 0; i < FlightSyncPermitLimit(); i++)
            Assert.Equal(HttpStatusCode.OK, (await client.PostAsync($"flights/{flight.Id}/sync", content: null)).StatusCode);

        var rejected = await client.PostAsync($"flights/{flight.Id}/sync", content: null);
        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        Assert.NotNull(rejected.Headers.RetryAfter);
    }

    /// <summary>Partitioned by traveler (oid), not by IP: every test client shares one IP, so a second
    /// traveler getting 200 proves one traveler using up their permits doesn't throttle another.</summary>
    [Fact]
    public async Task PostSync_RateLimit_TwoTravelers_DoNotShareABucket()
    {
        var (clientA, flightA) = await TravelerTrackingAFlightDaysAwayAsync();
        var (clientB, flightB) = await TravelerTrackingAFlightDaysAwayAsync();

        for (var i = 0; i < FlightSyncPermitLimit(); i++)
            await clientA.PostAsync($"flights/{flightA.Id}/sync", content: null);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await clientA.PostAsync($"flights/{flightA.Id}/sync", content: null)).StatusCode);

        Assert.Equal(HttpStatusCode.OK, (await clientB.PostAsync($"flights/{flightB.Id}/sync", content: null)).StatusCode);
    }

    private int FlightTrackPermitLimit() =>
        factory.Services.GetRequiredService<IConfiguration>().GetValue("RateLimiting:FlightTrack:PermitLimit", 10);

    /// <summary>Registers a traveler and returns a client plus a body that tracks successfully. The
    /// airline and airports must be seeded: reference rows seeded with <c>HasData</c> never reach this
    /// InMemory database, which is shared across the Integration collection — hence the existence guards
    /// and the codes no other test uses.</summary>
    private async Task<(HttpClient Client, TrackFlightRequest Body)> TravelerReadyToTrackAsync()
    {
        using (var scope = factory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FlightAssistantDbContext>();
            if (!db.Airlines.Any(a => a.Iata == "ZR"))
                db.Airlines.Add(Airline.Create("ZR", null, "Track Limit Air", "GB"));
            if (!db.Airports.Any(a => a.Iata == "ZRA"))
                db.Airports.Add(Airport.Create("ZRA", null, "Track Limit Origin", "Origin", "GB"));
            if (!db.Airports.Any(a => a.Iata == "ZRB"))
                db.Airports.Add(Airport.Create("ZRB", null, "Track Limit Destination", "Destination", "US"));
            await db.SaveChangesAsync();
        }

        var userId = Guid.NewGuid();
        var client = factory.CreateUserClient(userId.ToString());
        await client.PostAsJsonAsync("travelers", new { email = $"{userId}@example.com", displayName = "Test", countryCode = (string?)null });

        var serviceDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(30));
        var body = new TrackFlightRequest(
            $"ZR{Random.Shared.Next(1000, 9999)}", serviceDate, "ZRA", "ZRB",
            serviceDate.ToDateTime(new TimeOnly(9, 0), DateTimeKind.Utc),
            serviceDate.ToDateTime(new TimeOnly(12, 0), DateTimeKind.Utc));

        return (client, body);
    }

    [Fact]
    public async Task TrackFlight_RateLimit_Returns429WithRetryAfter_AfterTheConfiguredPermitCount()
    {
        var (client, body) = await TravelerReadyToTrackAsync();

        // The same flight every time: re-tracking one the traveler already tracks returns 201 and adds
        // nothing, so #380's cap (3 in appsettings.Testing.json) can never be what rejects these.
        for (var i = 0; i < FlightTrackPermitLimit(); i++)
            Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync("flights", body)).StatusCode);

        var rejected = await client.PostAsJsonAsync("flights", body);

        Assert.Equal(HttpStatusCode.TooManyRequests, rejected.StatusCode);
        Assert.NotNull(rejected.Headers.RetryAfter);
    }

    /// <summary>Partitioned by traveler (oid), not by IP: every test client shares one IP, so a second
    /// traveler getting 201 proves one traveler's burst doesn't throttle another.</summary>
    [Fact]
    public async Task TrackFlight_RateLimit_TwoTravelers_DoNotShareABucket()
    {
        var (clientA, bodyA) = await TravelerReadyToTrackAsync();
        var (clientB, bodyB) = await TravelerReadyToTrackAsync();

        for (var i = 0; i < FlightTrackPermitLimit(); i++)
            await clientA.PostAsJsonAsync("flights", bodyA);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await clientA.PostAsJsonAsync("flights", bodyA)).StatusCode);

        Assert.Equal(HttpStatusCode.Created, (await clientB.PostAsJsonAsync("flights", bodyB)).StatusCode);
    }

    [Fact]
    public async Task GetFlightStatus_NotTracking_Returns403_Not404()
    {
        // 403 even for a flight id that does not exist: the API does not reveal whether an id is
        // real to a traveler who does not track it (#405). The declared 404 was unreachable.
        var client = factory.CreateUserClient();
        var response = await client.GetAsync("flights/999999/status");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task AnErrorResponse_IsProblemJson()
    {
        // End-to-end: a DomainException through GlobalExceptionHandler must carry the media type
        // the spec declares (#415). Framework-produced problems already did; handler-produced
        // ones did not.
        var response = await factory.CreateUserClient().GetAsync("flights/999999/status");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task UntrackFlight_NotTracking_Returns204_Idempotently()
    {
        // Deleting what is already absent succeeds (#405); the declared 404 was unreachable.
        var client = factory.CreateUserClient();
        var response = await client.DeleteAsync("flights/999999");
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task ToggleNotifications_NotTracking_Returns404()
    {
        // The neighbour of the DELETE above, and deliberately different: patching an absent
        // resource is an error. Pinned so the asymmetry is a decision, not an accident.
        var client = factory.CreateUserClient();
        var response = await client.PatchAsJsonAsync("flights/999999/notify", new { notify = true });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>
    /// FlightTrackedVerificationEventHandler (#410) reacts to FlightTracked by queuing checks for
    /// the newly tracked flight's airline and airports; R5 requires it to swallow any failure so
    /// tracking itself never fails. Only an end-to-end test through the real MediatR pipeline can
    /// prove the exception is actually caught -- a unit test on the handler proves the same catch,
    /// but not that MediatR discovers and wires the handler at all, nor that the endpoint still
    /// answers 201 once the event has fired from inside the command's own SaveChangesAsync call.
    /// </summary>
    [Fact]
    public async Task TrackFlight_VerificationQueueThrows_StillReturns201()
    {
        var throwingQueue = Substitute.For<IVerificationQueue>();
        throwingQueue.EnqueueAsync(
                Arg.Any<VerificationEntityType>(), Arg.Any<IReadOnlyCollection<string>>(),
                Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<VerificationTrigger>(),
                Arg.Any<string>(), Arg.Any<decimal>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("boom"));

        using var scopedFactory = factory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services =>
            {
                services.AddScoped<IVerificationQueue>(_ => throwingQueue);

                // FlightAssistantWebApplicationFactory replaces FlightAssistantDbContext with a bare
                // InMemory provider and attaches no SaveChangesInterceptor at all (see its own comment,
                // and CLAUDE.md's #319 note that it "strips the interceptor entirely") -- without
                // DomainEventDispatchInterceptor, FlightTracked would never dispatch here and this test
                // would pass for the wrong reason (the event never firing, not the handler catching its
                // failure). Every other API test relies on interceptors being absent by default, so this
                // re-attaches one for this test's own host only, using the same strip-and-re-add pattern
                // the base factory itself uses to swap providers.
                var efCoreAssemblyPrefixes = new[] { "Microsoft.EntityFrameworkCore", "Npgsql.EntityFrameworkCore" };
                var toRemove = services.Where(d =>
                        d.ServiceType == typeof(FlightAssistantDbContext) ||
                        (d.ServiceType.Assembly.GetName().Name is { } n && efCoreAssemblyPrefixes.Any(n.StartsWith)))
                    .ToList();
                foreach (var d in toRemove) services.Remove(d);

                services.AddDbContext<FlightAssistantDbContext>((sp, opt) =>
                {
                    opt.UseInMemoryDatabase("FlightAssistantIntegrationTests");
                    opt.AddInterceptors(sp.GetRequiredService<DomainEventDispatchInterceptor>());
                });
            }));

        // Seed through the same scopedFactory the request will use, so there's no question of
        // whether the seed and the request land on the same in-memory database. "ZX"/"ZXA"/"ZXB":
        // grepped for `"Z[A-Z]{1,2}"` across FlightAssistant.API.Tests and confirmed unclaimed --
        // AdminVerificationEndpointTests already owns ZVA-ZVF (its class doc names this shared-DB
        // convention), which this test collided with under its previous ZV/ZVA/ZVB codes without
        // being caught, because the guard below reused the existing row on the collision instead
        // of failing on it. Seeded unconditionally now, not existence-guarded -- but EF Core
        // InMemory does not enforce unique indexes (confirmed directly: a model with
        // HasIndex(...).IsUnique() happily accepts a duplicate insert), so a future collision on
        // these codes would not throw at all. Both rows would simply persist in the shared
        // "FlightAssistantIntegrationTests" database, and a lookup by IATA code
        // (RefDataRepository.GetAirportIdByIataAsync) would return whichever one the provider
        // happened to enumerate first -- silently binding this test's assertions to an arbitrary
        // row instead of the one it just seeded. That is exactly why these codes were picked for
        // being unclaimed rather than relying on any constraint to catch a clash.
        //
        // The airline is seeded already verified (a real, non-import source) while both airports
        // are left unverified. FlightTrackedVerificationEventHandler queues the airline before the
        // airports (R7), inside the one try/catch that makes R5's swallow work, and throwingQueue
        // throws unconditionally for every call regardless of entity type -- so an unverified
        // airline would queue first, throw immediately, and the airport branch below it would
        // never run at all. Verifying the airline is what makes the airport EnqueueAsync call
        // (asserted below) the one that actually fires and throws.
        Airport departureAirport;
        Airport arrivalAirport;
        using (var scope = scopedFactory.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<FlightAssistantDbContext>();
            var airline = Airline.Create("ZX", null, "Verify Trigger Air", "GB");
            airline.Verify("admin", VerificationSources.Manual);
            db.Airlines.Add(airline);
            departureAirport = Airport.Create("ZXA", null, "Verify Trigger Origin", "Origin", "GB");
            arrivalAirport = Airport.Create("ZXB", null, "Verify Trigger Destination", "Destination", "US");
            db.Airports.Add(departureAirport);
            db.Airports.Add(arrivalAirport);
            await db.SaveChangesAsync();
        }

        var userId = Guid.NewGuid();
        var client = scopedFactory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", $"test-{userId}");
        await client.PostAsJsonAsync("travelers", new { email = $"{userId}@example.com", displayName = "Test", countryCode = (string?)null });

        var serviceDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(30));
        var body = new TrackFlightRequest(
            $"ZX{Random.Shared.Next(1000, 9999)}", serviceDate, "ZXA", "ZXB",
            serviceDate.ToDateTime(new TimeOnly(9, 0), DateTimeKind.Utc),
            serviceDate.ToDateTime(new TimeOnly(12, 0), DateTimeKind.Utc));

        var response = await client.PostAsJsonAsync("flights", body);

        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        // Proves the failure was actually reached -- a 201 alone can't tell "caught" apart from
        // "the handler never ran" (e.g. a DI wiring break), which is exactly what this test exists
        // to rule out. Pinned to VerificationEntityType.Airport with both seeded airports' row ids,
        // not Arg.Any<VerificationEntityType>(): the always-fresh airline alone satisfies a looser
        // assertion even if the airport leg of the handler never ran, which is what let the
        // ZV/ZVA/ZVB collision above go unnoticed.
        await throwingQueue.Received().EnqueueAsync(
            VerificationEntityType.Airport,
            Arg.Is<IReadOnlyCollection<string>>(keys =>
                keys.Count == 2 &&
                keys.Contains(departureAirport.Id.ToString(CultureInfo.InvariantCulture)) &&
                keys.Contains(arrivalAirport.Id.ToString(CultureInfo.InvariantCulture))),
            Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<VerificationTrigger>(),
            Arg.Any<string>(), Arg.Any<decimal>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task TrackFlight_UnknownAirline_Returns404()
    {
        // Undeclared before #405. One POST only: the Testing profile allows 2 before a 429.
        var client = factory.CreateUserClient();
        var tomorrow = DateTime.UtcNow.AddDays(1);
        var response = await client.PostAsJsonAsync("flights", new
        {
            flightNumber = "QQ123",
            serviceDate = DateOnly.FromDateTime(tomorrow).ToString("yyyy-MM-dd"),
            departureAirportIata = "LHR",
            arrivalAirportIata = "JFK",
            scheduledDepartureUtc = tomorrow.AddHours(9),
            scheduledArrivalUtc = tomorrow.AddHours(18)
        });
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }
}
