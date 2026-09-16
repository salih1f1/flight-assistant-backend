using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FlightAssistant.Application.Verification;
using FlightAssistant.Contracts.Paging;
using FlightAssistant.Contracts.Requests.Admin;
using FlightAssistant.Contracts.Responses.Admin;
using FlightAssistant.Domain.ReferenceData.Entities;
using FlightAssistant.Domain.Verification.Entities;
using FlightAssistant.Domain.Verification.Enums;
using FlightAssistant.Infrastructure.ExternalServices.Verification;
using FlightAssistant.Infrastructure.Persistance;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FlightAssistant.API.Tests.Integration;

/// <summary>
/// The four admin verification endpoints (#378) and <c>GET /admin/data/verification-requests</c>. They map
/// each handler's outcome onto a status; the rules themselves are covered by the Application handler tests,
/// and the audit row and unique-collision refusal by <c>AcceptVerificationSourcePostgresTests</c>, since this
/// host runs on InMemory with the audit interceptor stripped.
///
/// <para>
/// The host's real source registry is used: <c>ourairports</c> (Airport) and <c>mledoze</c> (Country). None of
/// these routes downloads a dataset: only the verification job calls <c>LookupAsync</c>, and the factory strips
/// the job; as a backstop, the factory also makes both source clients' transport throw
/// (<see cref="SourceHttpClients_RefuseToSend"/>). The InMemory database is shared by the whole "Integration" collection, so every test uses its own
/// entity keys (<see cref="NextKey"/>) and airport codes, and asserts on its own rows.
/// </para>
/// </summary>
[Collection("Integration")]
public class AdminVerificationEndpointTests(FlightAssistantWebApplicationFactory factory)
{
    private static readonly DateTime CheckedUtc = new(2026, 9, 15, 12, 0, 0, DateTimeKind.Utc);
    private static int _nextKey = 900_000_000;

    /// <summary>An Airport key no other test queues: keys are not checked for existence, so any positive id is valid.</summary>
    private static string NextKey() => Interlocked.Increment(ref _nextKey).ToString(CultureInfo.InvariantCulture);

    // ══════════════ No dataset downloads ══════════════

    /// <summary>
    /// The factory's structural backstop: the source clients' transport throws, so a test that reaches a real
    /// download fails instead of fetching a dataset. Sent to a closed local port, so without the backstop this
    /// is a refused connection, never a download.
    /// </summary>
    [Theory]
    [InlineData(OurAirportsVerificationSource.HttpClientName)]
    [InlineData(MledozeCountriesVerificationSource.HttpClientName)]
    public async Task SourceHttpClients_RefuseToSend(string clientName)
    {
        var client = factory.Services.GetRequiredService<IHttpClientFactory>().CreateClient(clientName);

        var refused = await Assert.ThrowsAsync<InvalidOperationException>(() => client.GetAsync("http://127.0.0.1:9/"));

        Assert.Equal(FlightAssistantWebApplicationFactory.VerificationDownloadRefused, refused.Message);
    }

    // ══════════════ GET /admin/verification-sources ══════════════

    [Fact]
    public async Task GetSources_Airport_Returns200_WithTheRegisteredAirportSourceAndItsCost()
    {
        var response = await factory.CreateAdminClient().GetAsync("admin/verification-sources?entityType=Airport");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var sources = await response.Content.ReadFromJsonAsync<List<VerificationSourceDto>>();
        Assert.Equal(new[] { new VerificationSourceDto("ourairports", 0m) }, sources);
    }

    [Fact]
    public async Task GetSources_Airline_Returns200_Empty()
    {
        var response = await factory.CreateAdminClient().GetAsync("admin/verification-sources?entityType=Airline");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty((await response.Content.ReadFromJsonAsync<List<VerificationSourceDto>>())!);
    }

    [Theory]
    [InlineData("admin/verification-sources?entityType=airport")]
    [InlineData("admin/verification-sources?entityType=")]
    [InlineData("admin/verification-sources")]
    public async Task GetSources_AnEntityTypeThatIsNotAnExactName_Returns400_UnderTheEntityTypeKey(string url)
    {
        var response = await factory.CreateAdminClient().GetAsync(url);

        await AssertValidationErrorsAsync(response, ("EntityType", "EntityType must be one of Airport, Airline, Country."));
    }

    [Fact]
    public async Task GetSources_Anonymous_Returns401()
    {
        var response = await factory.CreateClient().GetAsync("admin/verification-sources?entityType=Airport");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GetSources_AuthenticatedWithoutTheAdminRole_Returns403()
    {
        var response = await factory.CreateUserClient().GetAsync("admin/verification-sources?entityType=Airport");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ══════════════ GET /admin/verification-requests ══════════════

    [Fact]
    public async Task ListRequests_ReturnsTheEntitysChecks_WithTypedDifferencesAndSourceValues()
    {
        var airport = await SeedAirportAsync("ZVE", "List Test Airport");
        var key = airport.Id.ToString(CultureInfo.InvariantCulture);
        var mismatchId = await SeedMismatchAsync(airport, nameof(Airport.Name), "List Test Airport", "List Source Name");

        var response = await factory.CreateAdminClient().GetAsync($"admin/verification-requests?entityType=Airport&entityKey={key}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var rows = await response.Content.ReadFromJsonAsync<List<VerificationRequestDto>>();
        var row = Assert.Single(rows!, r => r.Id == mismatchId);
        Assert.Equal("Airport", row.EntityType);
        Assert.Equal(key, row.EntityKey);
        Assert.Equal("Mismatch", row.Status);
        var difference = Assert.Single(row.Differences);
        Assert.Equal(nameof(Airport.Name), difference.Column);
        Assert.Equal("List Test Airport", difference.Ours);
        Assert.Equal("List Source Name", difference.Source);
        Assert.Equal("ourairports", difference.SourceName);
        Assert.Equal("List Source Name", row.SourceValues["ourairports"][nameof(Airport.Name)]);
    }

    [Fact]
    public async Task ListRequests_AnEntityWithNoChecksYet_Returns200_Empty()
    {
        var response = await factory.CreateAdminClient().GetAsync($"admin/verification-requests?entityType=Airport&entityKey={NextKey()}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Empty((await response.Content.ReadFromJsonAsync<List<VerificationRequestDto>>())!);
    }

    [Fact]
    public async Task ListRequests_ABadEntityType_Returns400()
    {
        var response = await factory.CreateAdminClient().GetAsync("admin/verification-requests?entityType=Airports&entityKey=1");

        await AssertValidationErrorsAsync(response, ("EntityType", "EntityType must be one of Airport, Airline, Country."));
    }

    // ══════════════ POST /admin/verification-requests ══════════════

    [Fact]
    public async Task Queue_Returns202_WithQueuedAndSkipped_AndRecordsTheAdminFromTheClaimsAsRequestedBy()
    {
        var adminId = Guid.NewGuid().ToString();
        var (first, second) = (NextKey(), NextKey());

        var response = await factory.CreateAdminClient(adminId).PostAsJsonAsync("admin/verification-requests",
            new QueueVerificationRequestsRequest("Airport", new[] { first, second }, new[] { "ourairports" }));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal(new VerificationEnqueueDto(2, 0), await response.Content.ReadFromJsonAsync<VerificationEnqueueDto>());

        var rows = await RequestsForAsync(first, second);
        Assert.Equal(2, rows.Count);
        Assert.All(rows, row =>
        {
            Assert.Equal(VerificationEntityType.Airport, row.EntityType);
            Assert.Equal(VerificationRequestStatus.Pending, row.Status);
            Assert.Equal(VerificationTrigger.Admin, row.Trigger);
            Assert.Equal("ourairports", row.Sources);
            Assert.Equal(adminId, row.RequestedBy);
        });
    }

    [Fact]
    public async Task Queue_AnEntityWithAnOpenRequest_IsCountedAsSkipped()
    {
        var (open, fresh) = (NextKey(), NextKey());
        var client = factory.CreateAdminClient();
        await client.PostAsJsonAsync("admin/verification-requests",
            new QueueVerificationRequestsRequest("Airport", new[] { open }, new[] { "ourairports" }));

        var response = await client.PostAsJsonAsync("admin/verification-requests",
            new QueueVerificationRequestsRequest("Airport", new[] { open, fresh }, new[] { "ourairports" }));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal(new VerificationEnqueueDto(1, 1), await response.Content.ReadFromJsonAsync<VerificationEnqueueDto>());
    }

    /// <summary>
    /// The field name is <c>Keys[i]</c>, the index of the malformed key, with the domain key rule's own
    /// reason. Without that validation the queue throws <see cref="ArgumentException"/> on the key: a 500.
    /// </summary>
    [Fact]
    public async Task Queue_AMalformedKey_Returns400_UnderThatKeysIndexedName_AndQueuesNothing()
    {
        var valid = NextKey();

        var response = await factory.CreateAdminClient().PostAsJsonAsync("admin/verification-requests",
            new QueueVerificationRequestsRequest("Airport", new[] { valid, "0042" }, new[] { "ourairports" }));

        await AssertValidationErrorsAsync(response,
            ("Keys[1]", "An Airport key must be a positive integer id with no sign or leading zeros; got '0042'."));
        Assert.Empty(await RequestsForAsync(valid));
    }

    [Fact]
    public async Task Queue_AnInvalidEntityType_AndNoKeysOrSources_Returns400_UnderEachFieldsName()
    {
        var response = await factory.CreateAdminClient().PostAsJsonAsync("admin/verification-requests",
            new QueueVerificationRequestsRequest("Airports", Array.Empty<string>(), Array.Empty<string>()));

        await AssertValidationErrorsAsync(response,
            ("EntityType", "EntityType must be one of Airport, Airline, Country."),
            ("Keys", "At least one key is required."),
            ("Sources", "At least one source is required."));
    }

    [Theory]
    [InlineData("retired-source", "Source 'retired-source' is not registered.")]
    [InlineData("mledoze", "Source 'mledoze' does not support Airport.")]
    public async Task Queue_AnUnknownOrUnsupportedSource_Returns400_UnderTheSourcesKey(string source, string message)
    {
        var key = NextKey();

        var response = await factory.CreateAdminClient().PostAsJsonAsync("admin/verification-requests",
            new QueueVerificationRequestsRequest("Airport", new[] { key }, new[] { source }));

        await AssertValidationErrorsAsync(response, ("Sources", message));
        Assert.Empty(await RequestsForAsync(key));
    }

    // ══════════════ POST /admin/verification-requests/{id}/accept-source ══════════════

    [Fact]
    public async Task AcceptSource_Returns204_WritesTheSourcesValue_AndVerifiesTheEntityAsTheAdminFromTheClaims()
    {
        var adminId = Guid.NewGuid().ToString();
        var airport = await SeedAirportAsync("ZVA", "Accept Test Airport");
        var requestId = await SeedMismatchAsync(airport, nameof(Airport.Name), "Accept Test Airport", "Accepted Name Airport");

        var response = await factory.CreateAdminClient(adminId).PostAsJsonAsync(
            $"admin/verification-requests/{requestId}/accept-source", new AcceptVerificationSourceRequest("ourairports"));

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlightAssistantDbContext>();
        var updated = await db.Airports.AsNoTracking().SingleAsync(a => a.Id == airport.Id);
        Assert.Equal("Accepted Name Airport", updated.Name);
        Assert.True(updated.IsVerified);
        Assert.Equal(adminId, updated.VerifiedBy);
        Assert.Equal("ourairports", updated.VerificationSource);

        var request = await db.VerificationRequests.AsNoTracking().SingleAsync(r => r.Id == requestId);
        Assert.Equal(VerificationRequestStatus.Mismatch, request.Status);
    }

    [Fact]
    public async Task AcceptSource_ABlankSource_Returns400_UnderTheSourceKey()
    {
        var response = await factory.CreateAdminClient().PostAsJsonAsync(
            "admin/verification-requests/1/accept-source", new AcceptVerificationSourceRequest(""));

        await AssertValidationErrorsAsync(response, ("Source", "'Source' must not be empty."));
    }

    [Fact]
    public async Task AcceptSource_ASourceThatIsNotOneOfTheRequests_Returns400()
    {
        var airport = await SeedAirportAsync("ZVB", "Wrong Source Airport");
        var requestId = await SeedMismatchAsync(airport, nameof(Airport.Name), "Wrong Source Airport", "Other Name");

        var response = await factory.CreateAdminClient().PostAsJsonAsync(
            $"admin/verification-requests/{requestId}/accept-source", new AcceptVerificationSourceRequest("mledoze"));

        await AssertProblemAsync(response, HttpStatusCode.BadRequest,
            $"'mledoze' is not one of verification request {requestId}'s sources.");
    }

    [Fact]
    public async Task AcceptSource_AnUnknownRequest_Returns404()
    {
        var response = await factory.CreateAdminClient().PostAsJsonAsync(
            "admin/verification-requests/987654321/accept-source", new AcceptVerificationSourceRequest("ourairports"));

        await AssertProblemAsync(response, HttpStatusCode.NotFound, "Verification request 987654321 not found.");
    }

    [Fact]
    public async Task AcceptSource_AnEntityThatNoLongerExists_Returns404()
    {
        var key = NextKey();
        var requestId = await SeedRequestAsync(Mismatch(key,
            new[] { new VerificationDifference(nameof(Airport.Name), "Gone Airport", "Other Name", "ourairports") },
            new Dictionary<string, string?> { [nameof(Airport.Name)] = "Other Name" }));

        var response = await factory.CreateAdminClient().PostAsJsonAsync(
            $"admin/verification-requests/{requestId}/accept-source", new AcceptVerificationSourceRequest("ourairports"));

        await AssertProblemAsync(response, HttpStatusCode.NotFound, $"Airport {key} no longer exists.");
    }

    [Fact]
    public async Task AcceptSource_ARequestThatIsNotMismatch_Returns409()
    {
        var request = NewRequest(NextKey());
        request.MarkVerified(CheckedUtc);
        var requestId = await SeedRequestAsync(request);

        var response = await factory.CreateAdminClient().PostAsJsonAsync(
            $"admin/verification-requests/{requestId}/accept-source", new AcceptVerificationSourceRequest("ourairports"));

        await AssertProblemAsync(response, HttpStatusCode.Conflict,
            $"Verification request {requestId} is Verified; only a Mismatch can be accepted.");
    }

    [Fact]
    public async Task AcceptSource_ARowThatChangedSinceTheCheck_Returns409_AndWritesNothing()
    {
        var airport = await SeedAirportAsync("ZVC", "Renamed Since Airport");
        var requestId = await SeedMismatchAsync(airport, nameof(Airport.Name), "Name At The Check", "Source Name");

        var response = await factory.CreateAdminClient().PostAsJsonAsync(
            $"admin/verification-requests/{requestId}/accept-source", new AcceptVerificationSourceRequest("ourairports"));

        await AssertProblemAsync(response, HttpStatusCode.Conflict,
            $"Airport {airport.Id}'s Name has changed since the check; queue a new check.");

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlightAssistantDbContext>();
        var unchanged = await db.Airports.AsNoTracking().SingleAsync(a => a.Id == airport.Id);
        Assert.Equal("Renamed Since Airport", unchanged.Name);
        Assert.False(unchanged.IsVerified);
    }

    // ══════════════ POST /admin/verification-requests/{id}/retry ══════════════

    [Fact]
    public async Task Retry_Returns202_QueuingTheSameEntityAndSources_WithTheAdminFromTheClaimsAsRequestedBy()
    {
        var adminId = Guid.NewGuid().ToString();
        var key = NextKey();
        var earlier = NewRequest(key, requestedBy: "someone-else");
        earlier.MarkError("Entity no longer exists", CheckedUtc);
        var earlierId = await SeedRequestAsync(earlier);

        var response = await factory.CreateAdminClient(adminId).PostAsync($"admin/verification-requests/{earlierId}/retry", null);

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal(new VerificationEnqueueDto(1, 0), await response.Content.ReadFromJsonAsync<VerificationEnqueueDto>());

        var retried = Assert.Single(await RequestsForAsync(key), r => r.Id != earlierId);
        Assert.Equal(VerificationRequestStatus.Pending, retried.Status);
        Assert.Equal(VerificationTrigger.Admin, retried.Trigger);
        Assert.Equal("ourairports", retried.Sources);
        Assert.Equal(adminId, retried.RequestedBy);
    }

    [Fact]
    public async Task Retry_ANonPositiveId_Returns400_UnderTheRequestIdKey()
    {
        var response = await factory.CreateAdminClient().PostAsync("admin/verification-requests/0/retry", null);

        await AssertValidationErrorsAsync(response, ("RequestId", "'Request Id' must be greater than '0'."));
    }

    [Fact]
    public async Task Retry_ASourceThatIsNoLongerRegistered_Returns400()
    {
        var earlier = NewRequest(NextKey(), source: "retired-source");
        earlier.MarkError("Source 'retired-source' is not registered.", CheckedUtc);
        var earlierId = await SeedRequestAsync(earlier);

        var response = await factory.CreateAdminClient().PostAsync($"admin/verification-requests/{earlierId}/retry", null);

        await AssertProblemAsync(response, HttpStatusCode.BadRequest,
            $"Verification request {earlierId} cannot be retried: Source 'retired-source' is not registered.");
    }

    [Fact]
    public async Task Retry_AnUnknownRequest_Returns404()
    {
        var response = await factory.CreateAdminClient().PostAsync("admin/verification-requests/987654321/retry", null);

        await AssertProblemAsync(response, HttpStatusCode.NotFound, "Verification request 987654321 not found.");
    }

    [Fact]
    public async Task Retry_AnEntityThatAlreadyHasAnOpenRequest_Returns409()
    {
        var key = NextKey();
        var openId = await SeedRequestAsync(NewRequest(key));

        var response = await factory.CreateAdminClient().PostAsync($"admin/verification-requests/{openId}/retry", null);

        await AssertProblemAsync(response, HttpStatusCode.Conflict, $"Airport {key} already has an open verification request.");
        Assert.Single(await RequestsForAsync(key));
    }

    // ══════════════ GET /admin/data/verification-requests ══════════════

    [Fact]
    public async Task BrowseVerificationRequests_ReturnsARowJustQueued_WithEveryColumnButTheRowVersion()
    {
        var adminId = Guid.NewGuid().ToString();
        var key = NextKey();
        var client = factory.CreateAdminClient(adminId);
        await client.PostAsJsonAsync("admin/verification-requests",
            new QueueVerificationRequestsRequest("Airport", new[] { key }, new[] { "ourairports" }));

        var page = await BrowseAsync(client, $"admin/data/verification-requests?filter=EntityKey:{key}");

        var row = Assert.Single(page.Items);
        Assert.Equal(key, row[nameof(VerificationRequest.EntityKey)].GetString());
        Assert.Equal(adminId, row[nameof(VerificationRequest.RequestedBy)].GetString());
        Assert.Equal("ourairports", row[nameof(VerificationRequest.Sources)].GetString());
        Assert.Equal(
            new[]
            {
                "Id", "EntityType", "EntityKey", "Sources", "Trigger", "Status", "Attempts", "NextAttemptUtc",
                "DifferencesJson", "SourceValuesJson", "Error", "EstimatedCostUsd", "RequestedBy", "RequestedUtc",
                "StartedUtc", "CompletedUtc"
            }.Order(),
            row.Keys.Order());
    }

    [Fact]
    public async Task BrowseVerificationRequests_SortsById_AndFiltersByStatus()
    {
        var airport = await SeedAirportAsync("ZVD", "Browse Test Airport");
        var firstMismatch = await SeedMismatchAsync(airport, nameof(Airport.Name), "Browse Test Airport", "Browse A");
        var secondMismatch = await SeedMismatchAsync(airport, nameof(Airport.Name), "Browse Test Airport", "Browse B");
        var pending = await SeedRequestAsync(NewRequest(NextKey()));
        var client = factory.CreateAdminClient();

        // Newest first, so this test's rows (the newest Mismatch rows) are on the first page however many
        // the shared collection database holds.
        var mismatches = await BrowseAsync(client, "admin/data/verification-requests?filter=Status:Mismatch&sort=-Id&pageSize=200");

        var ids = mismatches.Items.Select(r => r[nameof(VerificationRequest.Id)].GetInt64()).ToList();
        Assert.Equal(new[] { secondMismatch, firstMismatch }, ids.Where(id => id == firstMismatch || id == secondMismatch));
        Assert.DoesNotContain(pending, ids);
        Assert.All(mismatches.Items, r => Assert.Equal((int)VerificationRequestStatus.Mismatch, r[nameof(VerificationRequest.Status)].GetInt32()));
        Assert.Equal(ids.OrderDescending(), ids);

        // Oldest first would put this test's rows past the first page once the shared database holds more
        // than a page of requests, so it is narrowed to this airport's key (a contains match on a string
        // column, so other keys containing it may also appear) and asserts the order of its own rows.
        var ascending = await BrowseAsync(client,
            $"admin/data/verification-requests?filter=EntityKey:{airport.Id.ToString(CultureInfo.InvariantCulture)}&sort=Id&pageSize=200");
        var ascendingIds = ascending.Items.Select(r => r[nameof(VerificationRequest.Id)].GetInt64()).ToList();
        Assert.Equal(new[] { firstMismatch, secondMismatch }, ascendingIds.Where(id => id == firstMismatch || id == secondMismatch));
        Assert.Equal(ascendingIds.Order(), ascendingIds);
    }

    // ── helpers ─────────────────────────────────────────────────────────────

    private static VerificationRequest NewRequest(string key, string source = "ourairports", string requestedBy = "admin-oid") =>
        VerificationRequest.Create(
            VerificationEntityType.Airport, key, new[] { source }, VerificationTrigger.Admin, requestedBy, 0m, CheckedUtc.AddMinutes(-5));

    private static VerificationRequest Mismatch(
        string key, IReadOnlyList<VerificationDifference> differences, IReadOnlyDictionary<string, string?> sourceValues)
    {
        var request = NewRequest(key);
        request.MarkMismatch(
            VerificationJson.SerializeDifferences(differences),
            VerificationJson.SerializeSourceValues(new Dictionary<string, IReadOnlyDictionary<string, string?>> { ["ourairports"] = sourceValues }),
            CheckedUtc);
        return request;
    }

    /// <summary>A Mismatch check of <paramref name="airport"/> by <c>ourairports</c> that differed on one column.</summary>
    private Task<long> SeedMismatchAsync(Airport airport, string column, string ours, string source)
    {
        var sourceValues = new Dictionary<string, string?>(VerificationCheckedColumns.Project(airport)) { [column] = source };
        return SeedRequestAsync(Mismatch(
            airport.Id.ToString(CultureInfo.InvariantCulture),
            new[] { new VerificationDifference(column, ours, source, "ourairports") },
            sourceValues));
    }

    private async Task<long> SeedRequestAsync(VerificationRequest request)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlightAssistantDbContext>();
        db.VerificationRequests.Add(request);
        await db.SaveChangesAsync();
        return request.Id;
    }

    private async Task<Airport> SeedAirportAsync(string iata, string name)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlightAssistantDbContext>();
        var airport = Airport.Create(iata, null, name, "Testville", "GB");
        db.Airports.Add(airport);
        await db.SaveChangesAsync();
        return airport;
    }

    private async Task<List<VerificationRequest>> RequestsForAsync(params string[] keys)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlightAssistantDbContext>();
        return await db.VerificationRequests.AsNoTracking()
            .Where(r => r.EntityType == VerificationEntityType.Airport && keys.Contains(r.EntityKey))
            .ToListAsync();
    }

    private static async Task<PagedResult<Dictionary<string, JsonElement>>> BrowseAsync(HttpClient client, string url)
    {
        var response = await client.GetAsync(url);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return (await response.Content.ReadFromJsonAsync<PagedResult<Dictionary<string, JsonElement>>>())!;
    }

    /// <summary>A 400 <c>ValidationProblemDetails</c> whose errors are exactly these field names and messages.</summary>
    private static async Task AssertValidationErrorsAsync(HttpResponseMessage response, params (string Field, string Message)[] expected)
    {
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var errors = problem.RootElement.GetProperty("errors").EnumerateObject()
            .ToDictionary(p => p.Name, p => p.Value.EnumerateArray().Select(m => m.GetString()).ToList());

        Assert.Equal(expected.Select(e => e.Field).Order(), errors.Keys.Order());
        foreach (var (field, message) in expected)
            Assert.Equal(new[] { message }, errors[field]);
    }

    /// <summary>A <c>ProblemDetails</c> at <paramref name="status"/> titled with the handler's message, and no validation errors.</summary>
    private static async Task AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status, string title)
    {
        Assert.Equal(status, response.StatusCode);
        using var problem = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(title, problem.RootElement.GetProperty("title").GetString());
        Assert.False(problem.RootElement.TryGetProperty("errors", out _));
    }
}
