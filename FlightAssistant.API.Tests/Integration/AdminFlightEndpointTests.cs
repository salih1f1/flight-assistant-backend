using System.Net;
using System.Net.Http.Json;
using FlightAssistant.Contracts.Responses.Admin;
using FlightAssistant.Domain.Flights.Entities;
using FlightAssistant.Infrastructure.Persistance;
using Microsoft.Extensions.DependencyInjection;

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
}
