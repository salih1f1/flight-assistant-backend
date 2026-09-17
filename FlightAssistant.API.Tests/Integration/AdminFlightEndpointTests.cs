using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FlightAssistant.Application.Commands.Flights;
using FlightAssistant.Application.Interfaces.ExternalServices;
using FlightAssistant.Contracts.Responses.Admin;
using FlightAssistant.Contracts.Responses.Flights;
using FlightAssistant.Domain.Flights.Entities;
using FlightAssistant.Infrastructure.Persistance;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;

namespace FlightAssistant.API.Tests.Integration;

/// <summary>
/// <c>GET /admin/flights/{id}/detail</c> (#322). Hangs off the shared <c>/admin</c> group, so the
/// auth cases here are really checking that it was mapped onto that group rather than standalone —
/// mirrors <c>AdminDataEndpointTests</c>' own note on the identical pattern.
///
/// <para>
/// <b>What this class deliberately does not cover.</b> A happy-path status event needs a real
/// <c>FlightStatus</c> row for its <c>StatusId</c> to resolve against — <c>flight_statuses</c> is
/// seeded via <c>HasData</c> (CLAUDE.md's "permanent ids" reference table), and <c>HasData</c> seed
/// rows are never materialised into <see cref="FlightAssistantWebApplicationFactory"/>'s InMemory
/// database at all (verified empirically: querying it here returns zero rows). <c>FlightStatus</c>
/// itself has no public constructor or factory (only EF's own materialiser ever populates one), so
/// there is no way for this InMemory-backed class to fabricate one either. The current-status
/// derivation, newest-first ordering, and the provider's-raw-status extraction are proved instead
/// against real PostgreSQL, where the real seeded rows exist, in
/// <c>AdminDrilldownRepositoryTests.FlightDetail_CurrentStatusIsTheNewestEvent_HistoryIsNewestFirst_AndProviderRawStatusComesFromRawPayload</c>
/// and its neighbours (Infrastructure.Tests). This class covers everything else reachable over real
/// HTTP: auth, 404, the flight-field mapping, and the no-events edge case.
/// </para>
/// </summary>
[Collection("Integration")]
public class AdminFlightEndpointTests(FlightAssistantWebApplicationFactory factory)
{
    private static string Marker() => Guid.NewGuid().ToString("N")[..8];

    private async Task<Flight> SeedFlightAsync(string flightNumber)
    {
        var flight = Flight.Create(1, flightNumber, 10, 20,
            new DateOnly(2026, 9, 20), DateTime.UtcNow, DateTime.UtcNow.AddHours(3),
            equipmentAircraftIcao: "A320");

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlightAssistantDbContext>();
        db.Flights.Add(flight);
        await db.SaveChangesAsync();
        return flight;
    }

    // ── Auth ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetDetail_Anonymous_Returns401()
    {
        var response = await factory.CreateClient().GetAsync("admin/flights/1/detail");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GetDetail_AuthenticatedWithoutTheAdminRole_Returns403()
    {
        var response = await factory.CreateUserClient().GetAsync("admin/flights/1/detail");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task GetDetail_Admin_UnknownId_Returns404()
    {
        var response = await factory.CreateAdminClient().GetAsync("admin/flights/999999999/detail");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ── Happy path (everything that doesn't need a real FlightStatus row) ───────

    [Fact]
    public async Task GetDetail_Admin_ReturnsFlightFields()
    {
        var flight = await SeedFlightAsync($"TK{Marker()}");

        var response = await factory.CreateAdminClient().GetAsync($"admin/flights/{flight.Id}/detail");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var dto = await response.Content.ReadFromJsonAsync<AdminFlightDetailDto>();
        Assert.NotNull(dto);
        Assert.Equal(flight.Id, dto!.Id);
        Assert.Equal(flight.AirlineId, dto.AirlineId);
        Assert.Equal(flight.FlightNumber, dto.FlightNumber);
        Assert.Equal(flight.DepartureAirportId, dto.DepartureAirportId);
        Assert.Equal(flight.ArrivalAirportId, dto.ArrivalAirportId);
        Assert.Equal(flight.ServiceDateUtc, dto.ServiceDate);
        Assert.Equal("A320", dto.EquipmentAircraftIcao);
    }

    [Fact]
    public async Task GetDetail_Admin_FlightWithNoStatusEvents_ReturnsNullCurrentStatus_AndEmptyHistory()
    {
        var flight = await SeedFlightAsync($"TK{Marker()}");

        var response = await factory.CreateAdminClient().GetAsync($"admin/flights/{flight.Id}/detail");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var dto = await response.Content.ReadFromJsonAsync<AdminFlightDetailDto>();
        Assert.NotNull(dto);
        Assert.Null(dto!.CurrentStatus);
        Assert.Empty(dto.StatusHistory);
    }

    // ══════════════ GET /admin/flights/search (#340) ══════════════

    private static string InThirtyDays() => DateOnly.FromDateTime(DateTime.UtcNow.AddDays(30)).ToString("yyyy-MM-dd");

    [Fact]
    public async Task Search_Anonymous_Returns401()
    {
        var response = await factory.CreateClient().GetAsync($"admin/flights/search?flightNumber=BA123&date={InThirtyDays()}");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Search_AuthenticatedWithoutTheAdminRole_Returns403()
    {
        var response = await factory.CreateUserClient().GetAsync($"admin/flights/search?flightNumber=BA123&date={InThirtyDays()}");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    /// <summary>
    /// The provider is faked, so this checks what reaches it (codes upper-cased, the codeshare flag
    /// carried through) and that its rows come back in the shape <c>POST /admin/travelers/{id}/flights</c>
    /// takes.
    /// </summary>
    [Fact]
    public async Task Search_Admin_SendsTheCriteriaToTheProvider_AndReturnsItsRows()
    {
        var serviceDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(30));
        var row = new FlightSearchResult(
            FlightNumber: "6E4120",
            ServiceDate: serviceDate,
            DepartureAirportIata: "SOF",
            ArrivalAirportIata: "IST",
            ScheduledDepartureUtc: serviceDate.ToDateTime(new TimeOnly(6, 35), DateTimeKind.Utc),
            ScheduledArrivalUtc: serviceDate.ToDateTime(new TimeOnly(8, 5), DateTimeKind.Utc));

        var provider = Substitute.For<IFlightSearchProvider>();
        provider.SearchAsync(Arg.Any<FlightSearchCriteria>(), Arg.Any<CancellationToken>()).Returns([row]);

        using var scopedFactory = factory.WithWebHostBuilder(builder =>
            builder.ConfigureServices(services => services.AddScoped<IFlightSearchProvider>(_ => provider)));
        var client = scopedFactory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", $"test-admin-{Guid.NewGuid()}");

        var response = await client.GetAsync(
            $"admin/flights/search?from=sof&to=ist&date={serviceDate:yyyy-MM-dd}&includeCodeshares=true");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await provider.Received(1).SearchAsync(
            new FlightSearchCriteria("SOF", "IST", serviceDate, null, IncludeCodeshares: true),
            Arg.Any<CancellationToken>());

        var dto = Assert.Single((await response.Content.ReadFromJsonAsync<List<FlightSearchResultDto>>())!);
        Assert.Equal(row.FlightNumber, dto.FlightNumber);
        Assert.Equal(row.ServiceDate, dto.ServiceDate);
        Assert.Equal(row.DepartureAirportIata, dto.DepartureAirportIata);
        Assert.Equal(row.ArrivalAirportIata, dto.ArrivalAirportIata);
        Assert.Equal(row.ScheduledDepartureUtc, dto.ScheduledDepartureUtc);
        Assert.Equal(row.ScheduledArrivalUtc, dto.ScheduledArrivalUtc);
    }

    [Fact]
    public async Task Search_Admin_NeitherRouteNorFlightNumber_Returns400()
    {
        var response = await factory.CreateAdminClient().GetAsync($"admin/flights/search?date={InThirtyDays()}");
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ── Sync (#356): the real host resolves the new handler dependencies ──

    /// <summary>
    /// The body this route answers. Until #443 it was a single <c>updated</c> boolean, asserted here
    /// as the *only* member; it is now <see cref="AdminFlightSyncResultDto"/>, and <c>updated</c> is
    /// gone rather than kept alongside — the traveler route is the one that still speaks that shape.
    /// </summary>
    [Fact]
    public async Task PostSync_Admin_UntrackedFlight_Returns200_WithTheSyncResult()
    {
        // Untracked, so the handler plans "none" and makes no provider call.
        var flight = await SeedFlightAsync($"TK{Marker()}");

        var response = await factory.CreateAdminClient().PostAsync($"admin/flights/{flight.Id}/sync", content: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(body.TryGetProperty("updated", out _));
        Assert.Equal("NotTracked", body.GetProperty("outcome").GetString());
        // No plan survives an untracked flight, so the route reports nulls rather than omitting them.
        Assert.Equal(JsonValueKind.Null, body.GetProperty("syncMode").ValueKind);
        Assert.Equal(JsonValueKind.Null, body.GetProperty("nextSyncDueUtc").ValueKind);
    }

    [Fact]
    public async Task PostSync_Admin_UnknownId_Returns404()
    {
        // Reachable here precisely because the admin route passes TravelerId: null, so the
        // traveler route's 403 gate is skipped and the flight lookup throws (#407).
        var response = await factory.CreateAdminClient().PostAsync("admin/flights/999999999/sync", content: null);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task PostSync_Admin_NonPositiveId_Returns400()
    {
        // Pins the validator's 400 this branch declared (#407): {id:int} matches 0 and negatives,
        // and SyncFlightStatusCommandValidator requires FlightId > 0.
        var response = await factory.CreateAdminClient().PostAsync("admin/flights/0/sync", content: null);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    // ══════════════ The sync contract (#443) ══════════════

    /// <summary>
    /// The route used to answer an untyped <c>{ updated }</c>, which the published spec declared as
    /// no body at all — so every generated client saw a void 200. It now answers
    /// <see cref="AdminFlightSyncResultDto"/>. A seeded flight nobody tracks reports
    /// <c>NotTracked</c> without the handler making any provider call, which is exactly one of the
    /// situations the old boolean could not express.
    /// </summary>
    [Fact]
    public async Task PostSync_Admin_ReturnsTheOutcome_NotABareBoolean()
    {
        var flight = await SeedFlightAsync($"TK{Marker()}");

        var response = await factory.CreateAdminClient().PostAsync($"admin/flights/{flight.Id}/sync", content: null);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var dto = await response.Content.ReadFromJsonAsync<AdminFlightSyncResultDto>();
        Assert.NotNull(dto);
        Assert.Equal("NotTracked", dto!.Outcome);
    }

    /// <summary>The outcome travels as its name, not its underlying number, so adding an outcome
    /// later cannot silently renumber the ones a client already reads.</summary>
    [Fact]
    public async Task PostSync_Admin_SendsTheOutcomeAsAName()
    {
        var flight = await SeedFlightAsync($"TK{Marker()}");

        var response = await factory.CreateAdminClient().PostAsync($"admin/flights/{flight.Id}/sync", content: null);

        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var outcome = json.RootElement.GetProperty("outcome");
        Assert.Equal(JsonValueKind.String, outcome.ValueKind);
        // A JSON string is not enough on its own: serialising the underlying number would still
        // produce one ("5"). What must hold is that the text is the member's name.
        Assert.False(int.TryParse(outcome.GetString(), out _));
        Assert.Contains(outcome.GetString(), Enum.GetNames<FlightStatusSyncOutcome>());
    }

    /// <summary>The five sync columns exist on the entity but were never projected, so the panel
    /// could only see them through the raw data browser.</summary>
    [Fact]
    public async Task GetDetail_Admin_ReturnsTheSyncFields()
    {
        var flight = await SeedFlightAsync($"TK{Marker()}");
        var syncedAt = new DateTime(2026, 9, 17, 9, 0, 0, DateTimeKind.Utc);
        await WithFlightAsync(flight.Id, f =>
        {
            f.MarkSynced(syncedAt);
            f.ApplySyncPlan(Domain.Flights.Enums.FlightSyncMode.Live, syncedAt.AddMinutes(30));
        });

        var response = await factory.CreateAdminClient().GetAsync($"admin/flights/{flight.Id}/detail");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var dto = await response.Content.ReadFromJsonAsync<AdminFlightDetailDto>();
        Assert.NotNull(dto);
        Assert.Equal(syncedAt, dto!.LastSyncedUtc);
        Assert.Equal(syncedAt.AddMinutes(30), dto.NextSyncDueUtc);
        Assert.Equal("Live", dto.SyncMode);
        Assert.Null(dto.ScheduleMissingSinceUtc);
        Assert.Null(dto.CancelledAtUtc);
    }

    /// <summary>The list row carries them too, so the grid can colour a row by how actively it
    /// syncs without fetching every flight's detail.</summary>
    [Fact]
    public async Task GetById_Admin_ReturnsTheSyncFields()
    {
        var flight = await SeedFlightAsync($"TK{Marker()}");
        var syncedAt = new DateTime(2026, 9, 17, 9, 0, 0, DateTimeKind.Utc);
        await WithFlightAsync(flight.Id, f =>
        {
            f.MarkSynced(syncedAt);
            f.ApplySyncPlan(Domain.Flights.Enums.FlightSyncMode.Fast, syncedAt.AddMinutes(3));
        });

        var response = await factory.CreateAdminClient().GetAsync($"admin/flights/{flight.Id}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var dto = await response.Content.ReadFromJsonAsync<AdminFlightDto>();
        Assert.NotNull(dto);
        Assert.Equal(syncedAt, dto!.LastSyncedUtc);
        Assert.Equal("Fast", dto.SyncMode);
    }

    /// <summary>Loads the tracked entity and applies a change, so a test can put a flight into a
    /// sync state the seeding factory method can't express.</summary>
    private async Task WithFlightAsync(int flightId, Action<Flight> change)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlightAssistantDbContext>();
        var flight = await db.Flights.FindAsync(flightId);
        change(flight!);
        await db.SaveChangesAsync();
    }
}
