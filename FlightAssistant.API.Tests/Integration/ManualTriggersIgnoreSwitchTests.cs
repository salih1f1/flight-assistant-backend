using System.Net;
using System.Net.Http.Json;
using FlightAssistant.Application.Jobs;
using FlightAssistant.Contracts.Requests.Admin;
using FlightAssistant.Domain.Flights.Entities;
using FlightAssistant.Domain.Jobs.Entities;
using FlightAssistant.Infrastructure.Persistance;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FlightAssistant.API.Tests.Integration;

/// <summary>
/// Pins a deliberate product decision (#471): switching a job off stops its SCHEDULED loop, but does
/// not seal the feature — an operator who switched a job off still needs a way to test it before
/// switching it back on, and taking that away would make the toggle something people hesitate to use.
///
/// <para>
/// <b>No production code here.</b> <c>IJobSwitch</c> is consulted only by
/// <c>FlightStatusSyncBackgroundService</c> and <c>ReferenceVerificationBackgroundService</c> (the
/// only two non-test, non-definition files that reference it, confirmed by grep before writing
/// this) — neither manual trigger below goes anywhere near it. Both tests are expected to pass on
/// first run; if either fails, Tasks 5-6 wired the switch into a manual trigger's own path rather
/// than only the loop, and the fix belongs in that production code, not in this test.
/// </para>
///
/// <para>
/// Job switches are set directly against <c>job_switches</c>, not through <c>PUT /admin/jobs/{job}</c>
/// — that endpoint is Task 8 of this same plan and does not exist yet when this task is done, and this
/// test has nothing to do with that endpoint anyway: it pins what the loop-vs-manual-trigger split does
/// once a switch row exists, however it got there.
/// </para>
/// </summary>
[Collection("Integration")]
public class ManualTriggersIgnoreSwitchTests(FlightAssistantWebApplicationFactory factory)
{
    private static int _nextKey;
    private static string NextKey() => Interlocked.Increment(ref _nextKey).ToString();

    /// <summary>Upserts rather than a bare Add: the "Integration" collection shares one InMemory
    /// database across every test class, so a prior test may already have touched this job's row —
    /// job_switches has exactly one row per job (JobNames.All is a closed list), and a blind Add
    /// against an already-tracked key throws.</summary>
    private async Task SwitchOffAsync(string job)
    {
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlightAssistantDbContext>();

        var existing = await db.JobSwitches.FirstOrDefaultAsync(s => s.Job == job);
        if (existing is null)
        {
            db.JobSwitches.Add(new JobSwitch
            {
                Job = job,
                Enabled = false,
                ChangedUtc = DateTime.UtcNow,
                ChangedBy = "test",
                Reason = nameof(ManualTriggersIgnoreSwitchTests),
            });
        }
        else
        {
            existing.Enabled = false;
        }

        await db.SaveChangesAsync();
    }

    /// <summary>30 days out, so the sync runs in Schedule mode and makes no live status call (mirrors
    /// FlightEndpointTests.TravelerTrackingAFlightDaysAwayAsync) — this test is about whether the
    /// switch is consulted, not about proving a particular sync outcome, so a call that might reach a
    /// live provider would only make it flakier without proving anything more. No traveler/tracking
    /// link is seeded: AdminFlightEndpoints' sync route passes TravelerId: null and, unlike the
    /// traveler route, never checks one.</summary>
    private async Task<int> TrackedFlightIdAsync()
    {
        var departure = DateTime.UtcNow.Date.AddDays(30).AddHours(9);
        var flight = Flight.Create(1, $"TK{Random.Shared.Next(1000, 9999)}", 10, 20,
            DateOnly.FromDateTime(departure), departure, departure.AddHours(3));

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<FlightAssistantDbContext>();
        db.Flights.Add(flight);
        await db.SaveChangesAsync();
        return flight.Id;
    }

    /// <summary>A syntactically valid, always-fresh Airport key. Queueing does not check the key
    /// exists — "a missing entity completes as Error when the job runs it" (AdminVerificationEndpoints'
    /// own doc comment) — so nothing needs seeding beyond the request itself.</summary>
    private static QueueVerificationRequestsRequest NewRequest() =>
        new("Airport", [NextKey()], ["ourairports"]);

    // Switching a job off stops its SCHEDULED loop. It does not seal the feature: an operator who
    // switched a job off needs a way to test it before switching it back on, and taking that away
    // would make the toggle something people hesitate to use.
    [Fact]
    public async Task ManualFlightSync_StillWorks_WhileTheLoopIsSwitchedOff()
    {
        await SwitchOffAsync(JobNames.FlightSync);
        var trackedFlightId = await TrackedFlightIdAsync();

        var response = await factory.CreateAdminClient().PostAsync($"admin/flights/{trackedFlightId}/sync", null);

        Assert.NotEqual(HttpStatusCode.Conflict, response.StatusCode);
        Assert.NotEqual(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public async Task ManualVerification_StillWorks_WhileTheLoopIsSwitchedOff()
    {
        await SwitchOffAsync(JobNames.Verification);

        var response = await factory.CreateAdminClient().PostAsJsonAsync("admin/verification-requests", NewRequest());

        Assert.NotEqual(HttpStatusCode.Conflict, response.StatusCode);
        Assert.NotEqual(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }
}
