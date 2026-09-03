using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FlightAssistant.Contracts.Paging;
using FlightAssistant.Contracts.Responses.Admin;
using FlightAssistant.Domain.Flights.Entities;
using FlightAssistant.Domain.Loyalty.Entities;
using FlightAssistant.Domain.Travelers.Entities;
using FlightAssistant.Infrastructure.Persistance;
using Microsoft.Extensions.DependencyInjection;

namespace FlightAssistant.API.Tests.Integration;

/// <summary>
/// <c>GET /admin/travelers</c>, <c>GET /admin/travelers/{id}</c> (#321),
/// <c>GET /admin/travelers/{id}/detail</c> and
/// <c>DELETE /admin/travelers/{travelerId}/device-tokens/{tokenId}</c> (#322). All hang off the
/// shared <c>/admin</c> group, so the auth cases here are really checking that they were mapped onto
/// that group rather than standalone — mirrors <c>AdminDataEndpointTests</c>' own note on the
/// identical pattern.
///
/// <para>
/// <b>The masking tests are the security-critical ones here</b> — same posture as
/// <c>AdminDataEndpointTests</c>: assert against the raw response body, not only a deserialized
/// DTO, and include a deliberately-unmasked control field from the same response so a passing
/// assertion is proven to discriminate rather than passing whether or not masking actually worked.
/// </para>
///
/// <para>
/// <b>The device-token delete's audit-row requirement is deliberately not tested here.</b>
/// <see cref="FlightAssistantWebApplicationFactory"/> strips <c>AdminAuditInterceptor</c> out of the
/// <c>DbContext</c> registration entirely (#319), so every save that runs through this factory
/// writes zero audit rows no matter the outcome — an assertion here could only ever pass trivially,
/// which is the opposite of proving anything. See
/// <c>AdminAuditInterceptorPostgresTests.SaveChangesAsync_DeviceTokenRemovedFromATrackedTraveler_WritesADeleteAuditRow_ForTheDeviceTokenNotTheTraveler</c>
/// (Infrastructure.Tests, PostgreSQL-backed) for that requirement instead.
/// </para>
/// </summary>
[Collection("Integration")]
public class AdminTravelerEndpointTests(FlightAssistantWebApplicationFactory factory)
{
    private static string Marker() => Guid.NewGuid().ToString("N");

    private async Task SeedAsync(Action<FlightAssistantDbContext> seed)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlightAssistantDbContext>();
        seed(db);
        await db.SaveChangesAsync();
    }

    /// <summary>
    /// Scans <c>GET /admin/travelers</c> page by page (ordered by <c>Email</c>, unfiltered — the
    /// route takes no filter) until <paramref name="travelerId"/> turns up. The "Integration"
    /// collection shares one fixed in-memory database
    /// (<see cref="FlightAssistantWebApplicationFactory"/>) across every test class in it, so by the
    /// time this runs an unpredictable number of other tests' travelers may already exist — page 1
    /// alone is not guaranteed to contain the one this test just seeded. Asserting
    /// <paramref name="rawEmail"/> absent on *every* page visited along the way is strictly stronger
    /// than checking one page, and failing loudly when the traveler is never found at all (rather
    /// than silently finishing the loop) is what stops this degrading into a vacuous pass if seeding
    /// or pagination ever breaks.
    /// </summary>
    private async Task<(string Body, AdminTravelerDto Dto)> FindTravelerAcrossAllPagesAsync(Guid travelerId, string rawEmail)
    {
        const int pageSize = 100;
        for (var page = 1; ; page++)
        {
            var response = await factory.CreateAdminClient().GetAsync($"admin/travelers?page={page}&pageSize={pageSize}");
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);

            var body = await response.Content.ReadAsStringAsync();
            Assert.DoesNotContain(rawEmail, body);

            var parsed = JsonSerializer.Deserialize<PagedResult<AdminTravelerDto>>(
                body, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            Assert.NotNull(parsed);

            var match = parsed!.Items.FirstOrDefault(i => i.Id == travelerId);
            if (match is not null) return (body, match);

            Assert.True(
                (long)page * pageSize < parsed.Total,
                $"Traveler {travelerId} was not found across {page} page(s) of {parsed.Total} total — seeding or paging is broken.");
        }
    }

    // ══════════════ GET /admin/travelers (#321) ══════════════
    // Auth (401/403/non-403-for-admin) for this route is already covered by AuthEndpointTests —
    // these two are specifically the #321 regression tests: before this fix, both routes below
    // returned Traveler.Email in the clear, making the admin data browser's masking (#315) a speed
    // bump rather than a control since the same value was one route away, unmasked.

    /// <summary>
    /// The bulk-list route #321 closes. Same discrimination proof as the <c>/detail</c> masking test
    /// below: assert against the raw response body, and check the unmasked <c>DisplayName</c> control
    /// from the very same page.
    /// </summary>
    [Fact]
    public async Task GetAll_Admin_MasksEmail_ButLeavesDisplayNameUnmasked()
    {
        var marker = Marker();
        var rawEmail = $"very-secret-{marker}@example.com";
        var displayName = $"Findable Traveler {marker}";
        var traveler = Traveler.Register(rawEmail, displayName);
        await SeedAsync(db => db.Travelers.Add(traveler));

        var (body, dto) = await FindTravelerAcrossAllPagesAsync(traveler.Id, rawEmail);

        Assert.Contains("<redacted>", body);
        Assert.Contains(displayName, body); // control: unmasked
        Assert.Equal("<redacted>", dto.Email);
        Assert.NotEqual(rawEmail, dto.Email);
        Assert.Equal(displayName, dto.DisplayName);
    }

    // ══════════════ GET /admin/travelers/{id} (#321) ══════════════

    /// <summary>The single-traveler route #321 closes, alongside the list above. Same
    /// discrimination proof: raw body check plus the unmasked <c>DisplayName</c> control.</summary>
    [Fact]
    public async Task GetById_Admin_MasksEmail_ButLeavesDisplayNameUnmasked()
    {
        var marker = Marker();
        var rawEmail = $"very-secret-{marker}@example.com";
        var displayName = $"Findable Traveler {marker}";
        var traveler = Traveler.Register(rawEmail, displayName);
        await SeedAsync(db => db.Travelers.Add(traveler));

        var response = await factory.CreateAdminClient().GetAsync($"admin/travelers/{traveler.Id}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(rawEmail, body);
        Assert.Contains("<redacted>", body);
        Assert.Contains(displayName, body); // control: unmasked

        var dto = await response.Content.ReadFromJsonAsync<AdminTravelerDto>();
        Assert.NotNull(dto);
        Assert.Equal("<redacted>", dto!.Email);
        Assert.NotEqual(rawEmail, dto.Email);
        Assert.Equal(displayName, dto.DisplayName);
    }

    // ══════════════ GET /admin/travelers/{id}/detail ══════════════

    // ── Auth ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetDetail_Anonymous_Returns401()
    {
        var response = await factory.CreateClient().GetAsync($"admin/travelers/{Guid.NewGuid()}/detail");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GetDetail_AuthenticatedWithoutTheAdminRole_Returns403()
    {
        var response = await factory.CreateUserClient().GetAsync($"admin/travelers/{Guid.NewGuid()}/detail");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task GetDetail_Admin_UnknownId_Returns404()
    {
        var response = await factory.CreateAdminClient().GetAsync($"admin/travelers/{Guid.NewGuid()}/detail");
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ── Masking: the security-critical test ─────────────────────────────────────

    /// <summary>
    /// Neither the raw email nor the raw device token ever leaves this endpoint.
    /// <c>DisplayName</c> (top level) and <c>Platform</c> (nested, on the device token) are checked
    /// unmasked from the very same response — proof this assertion discriminates: if masking were
    /// broken in a way that redacted everything (or the endpoint just returned an empty shell), the
    /// control checks would fail right alongside it instead of the test passing vacuously.
    /// </summary>
    [Fact]
    public async Task GetDetail_Admin_MasksEmailAndToken_ButLeavesDisplayNameAndPlatformUnmasked()
    {
        var marker = Marker();
        var rawEmail = $"very-secret-{marker}@example.com";
        var rawToken = $"very-secret-token-{marker}";
        var traveler = Traveler.Register(rawEmail, $"Findable Traveler {marker}");
        traveler.AddDeviceToken("ios", rawToken);
        await SeedAsync(db => db.Travelers.Add(traveler));

        var response = await factory.CreateAdminClient().GetAsync($"admin/travelers/{traveler.Id}/detail");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(rawEmail, body);
        Assert.DoesNotContain(rawToken, body);
        Assert.Contains("<redacted>", body);
        Assert.Contains($"Findable Traveler {marker}", body); // control: unmasked
        Assert.Contains("ios", body); // control: unmasked

        var dto = await response.Content.ReadFromJsonAsync<AdminTravelerDetailDto>();
        Assert.NotNull(dto);
        Assert.Equal("<redacted>", dto!.Email);
        Assert.NotEqual(rawEmail, dto.Email);
        Assert.Equal($"Findable Traveler {marker}", dto.DisplayName);

        var token = Assert.Single(dto.DeviceTokens);
        Assert.Equal("<redacted>", token.Token);
        Assert.NotEqual(rawToken, token.Token);
        Assert.Equal("ios", token.Platform);
    }

    /// <summary>The account number is masked the same way, for the same reason — one registry
    /// (<c>SensitiveProperties</c>) decides what's sensitive everywhere, not a hand-picked subset.
    /// <c>ProgramId</c> is the unmasked control.</summary>
    [Fact]
    public async Task GetDetail_Admin_MasksLoyaltyAccountNumber_ButLeavesProgramIdUnmasked()
    {
        var marker = Marker();
        var traveler = Traveler.Register($"{marker}@example.com");
        await SeedAsync(db => db.Travelers.Add(traveler));

        var rawAccountNumber = $"ACCT-SECRET-{marker}";
        await SeedAsync(db => db.TravelerLoyaltyMemberships.Add(
            TravelerLoyaltyMembership.Create(traveler.Id, programId: 42, accountNumber: rawAccountNumber, tierId: 9)));

        var response = await factory.CreateAdminClient().GetAsync($"admin/travelers/{traveler.Id}/detail");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var body = await response.Content.ReadAsStringAsync();
        Assert.DoesNotContain(rawAccountNumber, body);
        Assert.Contains("<redacted>", body);

        var dto = await response.Content.ReadFromJsonAsync<AdminTravelerDetailDto>();
        Assert.NotNull(dto);
        var membership = Assert.Single(dto!.LoyaltyMemberships);
        Assert.Equal("<redacted>", membership.AccountNumber);
        Assert.NotEqual(rawAccountNumber, membership.AccountNumber);
        Assert.Equal(42, membership.ProgramId); // control: unmasked
    }

    // ── Relationship assembly ────────────────────────────────────────────────────

    [Fact]
    public async Task GetDetail_Admin_ReturnsTrackedFlights_JoinedWithTheFlightRow()
    {
        var marker = Marker();
        var traveler = Traveler.Register($"{marker}@example.com");
        await SeedAsync(db => db.Travelers.Add(traveler));

        var flight = Flight.Create(1, $"TK{marker[..4]}", 10, 20,
            new DateOnly(2026, 9, 15), DateTime.UtcNow, DateTime.UtcNow.AddHours(2));
        await SeedAsync(db => db.Flights.Add(flight));
        await SeedAsync(db => db.TravelerFlights.Add(TravelerFlight.Create(traveler.Id, flight.Id)));

        var response = await factory.CreateAdminClient().GetAsync($"admin/travelers/{traveler.Id}/detail");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var dto = await response.Content.ReadFromJsonAsync<AdminTravelerDetailDto>();
        Assert.NotNull(dto);
        var trackedFlight = Assert.Single(dto!.TrackedFlights);
        Assert.Equal(flight.Id, trackedFlight.FlightId);
        Assert.Equal(flight.FlightNumber, trackedFlight.FlightNumber);
    }

    [Fact]
    public async Task GetDetail_Admin_TravelerWithNoRelationships_ReturnsEmptyLists()
    {
        var traveler = Traveler.Register($"{Marker()}@example.com");
        await SeedAsync(db => db.Travelers.Add(traveler));

        var response = await factory.CreateAdminClient().GetAsync($"admin/travelers/{traveler.Id}/detail");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var dto = await response.Content.ReadFromJsonAsync<AdminTravelerDetailDto>();
        Assert.NotNull(dto);
        Assert.Empty(dto!.DeviceTokens);
        Assert.Empty(dto.TrackedFlights);
        Assert.Empty(dto.LoyaltyMemberships);
    }

    // ══════════════ DELETE /admin/travelers/{travelerId}/device-tokens/{tokenId} ══════════════

    // ── Auth ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task DeleteDeviceToken_Anonymous_Returns401()
    {
        var response = await factory.CreateClient()
            .DeleteAsync($"admin/travelers/{Guid.NewGuid()}/device-tokens/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task DeleteDeviceToken_AuthenticatedWithoutTheAdminRole_Returns403()
    {
        var response = await factory.CreateUserClient()
            .DeleteAsync($"admin/travelers/{Guid.NewGuid()}/device-tokens/{Guid.NewGuid()}");
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task DeleteDeviceToken_Admin_TokenExists_Returns204()
    {
        var traveler = Traveler.Register($"{Marker()}@example.com");
        var token = traveler.AddDeviceToken("ios", $"token-{Marker()}");
        await SeedAsync(db => db.Travelers.Add(traveler));

        var response = await factory.CreateAdminClient()
            .DeleteAsync($"admin/travelers/{traveler.Id}/device-tokens/{token.Id}");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
    }

    [Fact]
    public async Task DeleteDeviceToken_Admin_TokenExists_ActuallyRemovesIt()
    {
        var traveler = Traveler.Register($"{Marker()}@example.com");
        var keep = traveler.AddDeviceToken("ios", $"keep-{Marker()}");
        var remove = traveler.AddDeviceToken("android", $"remove-{Marker()}");
        await SeedAsync(db => db.Travelers.Add(traveler));

        var response = await factory.CreateAdminClient()
            .DeleteAsync($"admin/travelers/{traveler.Id}/device-tokens/{remove.Id}");
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        var detail = await factory.CreateAdminClient().GetAsync($"admin/travelers/{traveler.Id}/detail");
        var dto = await detail.Content.ReadFromJsonAsync<AdminTravelerDetailDto>();
        Assert.NotNull(dto);
        var remaining = Assert.Single(dto!.DeviceTokens);
        Assert.Equal(keep.Id, remaining.Id);
    }

    [Fact]
    public async Task DeleteDeviceToken_Admin_UnknownTraveler_Returns404()
    {
        var response = await factory.CreateAdminClient()
            .DeleteAsync($"admin/travelers/{Guid.NewGuid()}/device-tokens/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task DeleteDeviceToken_Admin_UnknownToken_Returns404_AndDoesNotTouchOtherTokens()
    {
        var traveler = Traveler.Register($"{Marker()}@example.com");
        var untouched = traveler.AddDeviceToken("ios", $"token-{Marker()}");
        await SeedAsync(db => db.Travelers.Add(traveler));

        var response = await factory.CreateAdminClient()
            .DeleteAsync($"admin/travelers/{traveler.Id}/device-tokens/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);

        var detail = await factory.CreateAdminClient().GetAsync($"admin/travelers/{traveler.Id}/detail");
        var dto = await detail.Content.ReadFromJsonAsync<AdminTravelerDetailDto>();
        var remaining = Assert.Single(dto!.DeviceTokens);
        Assert.Equal(untouched.Id, remaining.Id);
    }
}
