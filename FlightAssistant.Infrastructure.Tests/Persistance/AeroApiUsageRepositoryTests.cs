using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Domain.Configuration.Entities;
using FlightAssistant.Infrastructure.Persistance;
using FlightAssistant.Infrastructure.Persistance.Repositories;
using Microsoft.EntityFrameworkCore;

namespace FlightAssistant.Infrastructure.Tests.Persistance;

/// <summary>
/// Covers the two counts the rollup's money and health figures are built from: which rows count as
/// <i>billable</i>, and which count as a transport failure. Both are easy to get subtly wrong in a
/// way that reads plausibly on a dashboard and is wrong on the invoice.
///
/// <para>
/// Backed by the InMemory provider, like the rest of this project's persistence tests — it verifies
/// what the grouping <i>means</i>, not that Npgsql translates it. There is no integration-test
/// harness in this solution to prove the latter.
/// </para>
/// </summary>
public class AeroApiUsageRepositoryTests
{
    private static readonly DateTime Anchor = new(2026, 8, 15, 12, 0, 0, DateTimeKind.Utc);

    private static FlightAssistantDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<FlightAssistantDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static AeroApiCall Call(
        string template, AeroApiCallOutcome outcome, int? statusCode, DateTime calledUtc) =>
        AeroApiCall.Record(template, "GET", statusCode, 10, outcome, "search", calledUtc);

    /// <summary>The window every fixture row below sits inside.</summary>
    private static Task<AeroApiUsageRawData> RollupAsync(AeroApiUsageRepository repo)
    {
        var from = Anchor.AddDays(-1);
        var to = Anchor.AddDays(1);
        return repo.GetUsageRollupAsync(from, to, from, to, from, to);
    }

    [Fact]
    public async Task BillableCounts_ExcludeThrottledRows_WhileTheTotalStillCountsThem()
    {
        // A 429 is refused by FlightAware before the query is served, so it is not billed. Pricing
        // it anyway makes an AeroAPI outage look like a spend spike, which is the opposite of what
        // the meter exists to show.
        using var ctx = CreateContext();
        ctx.AeroApiCalls.AddRange(
            Call("/schedules/{range}", AeroApiCallOutcome.Success, 200, Anchor),
            Call("/schedules/{range}", AeroApiCallOutcome.Success, 200, Anchor),
            Call("/schedules/{range}", AeroApiCallOutcome.Throttled, 429, Anchor),
            Call("/schedules/{range}", AeroApiCallOutcome.Throttled, 429, Anchor),
            // Failed stays INSIDE the billable count on purpose: a 4xx is plausibly billed and a
            // 5xx plausibly is not, and only the invoice settles it — so it is kept, not guessed away.
            Call("/schedules/{range}", AeroApiCallOutcome.Failed, 400, Anchor),
            Call("/flights/{ident}", AeroApiCallOutcome.Throttled, 429, Anchor));
        await ctx.SaveChangesAsync();

        var raw = await RollupAsync(new AeroApiUsageRepository(ctx));

        Assert.Equal(5, raw.CallsByTemplate["/schedules/{range}"]);
        Assert.Equal(3, raw.BillableCallsByTemplate["/schedules/{range}"]);

        // A template whose every call was throttled still appears, at zero — it is a real
        // observation ("we tried 1 time and were refused"), not an absence.
        Assert.Equal(1, raw.CallsByTemplate["/flights/{ident}"]);
        Assert.Equal(0, raw.BillableCallsByTemplate["/flights/{ident}"]);
    }

    [Fact]
    public async Task BillableCounts_MatchTotals_WhenNothingWasThrottled()
    {
        using var ctx = CreateContext();
        ctx.AeroApiCalls.AddRange(
            Call("/airports/{id}", AeroApiCallOutcome.Success, 200, Anchor),
            Call("/airports/{id}", AeroApiCallOutcome.Failed, null, Anchor));
        await ctx.SaveChangesAsync();

        var raw = await RollupAsync(new AeroApiUsageRepository(ctx));

        Assert.Equal(raw.CallsByTemplate, raw.BillableCallsByTemplate);
    }

    [Fact]
    public async Task TransportFailures_CountFailedRowsWithNoStatus_NotUpstream502s()
    {
        // This app's 502 is a MAPPING over an AeroAPI call that was unreachable or unparseable, and
        // the recording handler stores that as Outcome=Failed with no status code. AeroAPI itself
        // does not send 502s, so the old `StatusCode == 502` predicate reported 0 on exactly the
        // days clients were being served 502s.
        using var ctx = CreateContext();
        ctx.AeroApiCalls.AddRange(
            Call("/flights/{ident}", AeroApiCallOutcome.Failed, null, Anchor),
            Call("/flights/{ident}", AeroApiCallOutcome.Failed, null, Anchor),
            // Had a response, so not a transport failure however it was mapped downstream.
            Call("/flights/{ident}", AeroApiCallOutcome.Failed, 500, Anchor),
            Call("/flights/{ident}", AeroApiCallOutcome.Failed, 502, Anchor),
            Call("/flights/{ident}", AeroApiCallOutcome.Throttled, 429, Anchor),
            Call("/flights/{ident}", AeroApiCallOutcome.Success, 200, Anchor));
        await ctx.SaveChangesAsync();

        var raw = await RollupAsync(new AeroApiUsageRepository(ctx));

        Assert.Equal(2, raw.TransportFailureCountLast7Days);
        Assert.Equal(1, raw.Status429CountLast7Days);
    }

    [Fact]
    public async Task EveryCount_IsBoundedByTheWindowItWasAskedFor()
    {
        using var ctx = CreateContext();
        ctx.AeroApiCalls.AddRange(
            Call("/schedules/{range}", AeroApiCallOutcome.Success, 200, Anchor),
            Call("/schedules/{range}", AeroApiCallOutcome.Throttled, 429, Anchor),
            Call("/schedules/{range}", AeroApiCallOutcome.Failed, null, Anchor),
            // Outside every window below.
            Call("/schedules/{range}", AeroApiCallOutcome.Success, 200, Anchor.AddDays(-30)),
            Call("/schedules/{range}", AeroApiCallOutcome.Failed, null, Anchor.AddDays(-30)));
        await ctx.SaveChangesAsync();

        var raw = await RollupAsync(new AeroApiUsageRepository(ctx));

        Assert.Equal(3, raw.CallsByTemplate["/schedules/{range}"]);
        Assert.Equal(2, raw.BillableCallsByTemplate["/schedules/{range}"]);
        Assert.Equal(1, raw.TransportFailureCountLast7Days);
    }

    [Fact]
    public async Task AnEmptyTable_YieldsEmptyGroupingsRatherThanThrowing()
    {
        using var ctx = CreateContext();

        var raw = await RollupAsync(new AeroApiUsageRepository(ctx));

        Assert.Empty(raw.CallsByTemplate);
        Assert.Empty(raw.BillableCallsByTemplate);
        Assert.Equal(0, raw.TransportFailureCountLast7Days);
        Assert.Equal(0, raw.Status429CountLast7Days);
    }
}
