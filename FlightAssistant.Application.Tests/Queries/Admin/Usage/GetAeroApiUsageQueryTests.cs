using FlightAssistant.Application.Interfaces.Configuration;
using FlightAssistant.Application.Interfaces.ExternalServices;
using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Application.Queries.Admin.Usage;
using FlightAssistant.Domain.Configuration.Entities;
using NSubstitute;

namespace FlightAssistant.Application.Tests.Queries.Admin.Usage;

public class GetAeroApiUsageQueryTests
{
    private sealed class FakePricingOptions : IAeroApiUsagePricingOptions
    {
        public IReadOnlyDictionary<string, decimal> Pricing { get; init; } = new Dictionary<string, decimal>();
        public decimal MonthlyBudgetUsd { get; init; } = 5m;
    }

    private readonly IAeroApiUsageRepository _repo = Substitute.For<IAeroApiUsageRepository>();
    private readonly IAeroApiThrottleObserver _throttle = Substitute.For<IAeroApiThrottleObserver>();

    [Fact]
    public async Task Handle_RollsUpCallsAcrossTemplatesAndOutcomes_WithPricedSpendAndShare()
    {
        var raw = new AeroApiUsageRawData(
            CallsByTemplate: new Dictionary<string, int>
            {
                ["/schedules/{range}"] = 30,
                ["/flights/{ident}"] = 10,
            },
            // 3 of the 40 were throttled: 2 schedules calls and 1 flights call.
            BillableCallsByTemplate: new Dictionary<string, int>
            {
                ["/schedules/{range}"] = 28,
                ["/flights/{ident}"] = 9,
            },
            CallsByOutcome: new Dictionary<AeroApiCallOutcome, int>
            {
                [AeroApiCallOutcome.Success] = 35,
                [AeroApiCallOutcome.Throttled] = 3,
                [AeroApiCallOutcome.Failed] = 2,
            },
            CallsByDay: new Dictionary<DateOnly, int>
            {
                [new DateOnly(2026, 8, 1)] = 20,
                [new DateOnly(2026, 8, 2)] = 20,
            },
            CallsByTemplateForCurrentMonth: new Dictionary<string, int>
            {
                ["/schedules/{range}"] = 30,
                ["/flights/{ident}"] = 10,
            },
            Status429CountLast7Days: 3,
            TransportFailureCountLast7Days: 1);
        _repo.GetUsageRollupAsync(
                Arg.Any<DateTime>(), Arg.Any<DateTime>(),
                Arg.Any<DateTime>(), Arg.Any<DateTime>(),
                Arg.Any<DateTime>(), Arg.Any<DateTime>(),
                Arg.Any<CancellationToken>())
            .Returns(raw);
        var pricing = new FakePricingOptions
        {
            Pricing = new Dictionary<string, decimal>
            {
                ["/schedules/{range}"] = 0.01m,
                ["/flights/{ident}"] = 0.02m,
            },
            MonthlyBudgetUsd = 5m,
        };
        var handler = new GetAeroApiUsageQueryHandler(_repo, pricing, _throttle);

        var result = await handler.Handle(new GetAeroApiUsageQuery(null, null), CancellationToken.None);

        Assert.Equal(40, result.TotalCalls);
        // 30 * 0.01 + 10 * 0.02 = 0.5 — the upper bound: every call, throttled ones included.
        Assert.Equal(0.5m, result.EstimatedSpendUsd);
        // 28 * 0.01 + 9 * 0.02 = 0.46 — the lower bound, with the 3 throttled calls dropped.
        Assert.Equal(0.46m, result.BillableSpendUsd);
        Assert.True(result.BillableSpendUsd < result.EstimatedSpendUsd);
        Assert.Equal(5m, result.MonthlyBudgetUsd);
        Assert.Equal(40, result.CurrentMonthCalls);
        Assert.Equal(0.5m, result.CurrentMonthSpendUsd);

        Assert.Equal(2, result.ByTemplate.Count);
        var schedules = result.ByTemplate.Single(t => t.EndpointTemplate == "/schedules/{range}");
        Assert.Equal(30, schedules.Calls);
        Assert.Equal(0.3m, schedules.SpendUsd);
        Assert.Equal(0.75m, schedules.Share); // 30 / 40
        var flights = result.ByTemplate.Single(t => t.EndpointTemplate == "/flights/{ident}");
        Assert.Equal(10, flights.Calls);
        Assert.Equal(0.2m, flights.SpendUsd);
        Assert.Equal(0.25m, flights.Share); // 10 / 40

        Assert.Equal(3, result.ByOutcome.Count);
        Assert.Equal(35, result.ByOutcome.Single(o => o.Outcome == nameof(AeroApiCallOutcome.Success)).Calls);
        Assert.Equal(3, result.ByOutcome.Single(o => o.Outcome == nameof(AeroApiCallOutcome.Throttled)).Calls);
        Assert.Equal(2, result.ByOutcome.Single(o => o.Outcome == nameof(AeroApiCallOutcome.Failed)).Calls);

        Assert.Equal(2, result.DailyCounts.Count);
        Assert.Equal(20, result.DailyCounts[0].Calls);

        Assert.Equal(3, result.Status429CountLast7Days);
        Assert.Equal(1, result.TransportFailureCountLast7Days);
    }

    [Fact]
    public async Task Handle_AnAllThrottledRange_ShowsAnUpperBoundSpendButZeroBillableSpend()
    {
        // The AeroAPI-outage case the two figures exist for: every call in the window was refused
        // with a 429, which FlightAware rejects before serving and does not bill. EstimatedSpendUsd
        // still prices them (it is deliberately the ceiling), so BillableSpendUsd is the only thing
        // telling the operator this "spend spike" cost nothing at all.
        var raw = new AeroApiUsageRawData(
            CallsByTemplate: new Dictionary<string, int> { ["/schedules/{range}"] = 50 },
            BillableCallsByTemplate: new Dictionary<string, int> { ["/schedules/{range}"] = 0 },
            CallsByOutcome: new Dictionary<AeroApiCallOutcome, int> { [AeroApiCallOutcome.Throttled] = 50 },
            CallsByDay: new Dictionary<DateOnly, int> { [new DateOnly(2026, 8, 1)] = 50 },
            CallsByTemplateForCurrentMonth: new Dictionary<string, int> { ["/schedules/{range}"] = 50 },
            Status429CountLast7Days: 50,
            TransportFailureCountLast7Days: 0);
        _repo.GetUsageRollupAsync(
                Arg.Any<DateTime>(), Arg.Any<DateTime>(),
                Arg.Any<DateTime>(), Arg.Any<DateTime>(),
                Arg.Any<DateTime>(), Arg.Any<DateTime>(),
                Arg.Any<CancellationToken>())
            .Returns(raw);
        var pricing = new FakePricingOptions
        {
            Pricing = new Dictionary<string, decimal> { ["/schedules/{range}"] = 0.01m },
        };
        var handler = new GetAeroApiUsageQueryHandler(_repo, pricing, _throttle);

        var result = await handler.Handle(new GetAeroApiUsageQuery(null, null), CancellationToken.None);

        Assert.Equal(50, result.TotalCalls);
        Assert.Equal(0.5m, result.EstimatedSpendUsd);
        Assert.Equal(0m, result.BillableSpendUsd);
    }

    [Fact]
    public async Task Handle_NothingThrottled_MakesTheTwoSpendFiguresAgree()
    {
        // The other end of the bracket: with no throttled rows the ceiling and the floor are the
        // same number, so an operator seeing them diverge knows it is throttling, not drift.
        // Failed rows stay inside BOTH figures — a 4xx is plausibly billed, and only the invoice
        // can settle it.
        var raw = new AeroApiUsageRawData(
            CallsByTemplate: new Dictionary<string, int> { ["/flights/{ident}"] = 20 },
            BillableCallsByTemplate: new Dictionary<string, int> { ["/flights/{ident}"] = 20 },
            CallsByOutcome: new Dictionary<AeroApiCallOutcome, int>
            {
                [AeroApiCallOutcome.Success] = 12,
                [AeroApiCallOutcome.Failed] = 8,
            },
            CallsByDay: new Dictionary<DateOnly, int> { [new DateOnly(2026, 8, 1)] = 20 },
            CallsByTemplateForCurrentMonth: new Dictionary<string, int> { ["/flights/{ident}"] = 20 },
            Status429CountLast7Days: 0,
            TransportFailureCountLast7Days: 8);
        _repo.GetUsageRollupAsync(
                Arg.Any<DateTime>(), Arg.Any<DateTime>(),
                Arg.Any<DateTime>(), Arg.Any<DateTime>(),
                Arg.Any<DateTime>(), Arg.Any<DateTime>(),
                Arg.Any<CancellationToken>())
            .Returns(raw);
        var pricing = new FakePricingOptions
        {
            Pricing = new Dictionary<string, decimal> { ["/flights/{ident}"] = 0.02m },
        };
        var handler = new GetAeroApiUsageQueryHandler(_repo, pricing, _throttle);

        var result = await handler.Handle(new GetAeroApiUsageQuery(null, null), CancellationToken.None);

        Assert.Equal(0.4m, result.EstimatedSpendUsd);
        Assert.Equal(0.4m, result.BillableSpendUsd);
        Assert.Equal(8, result.TransportFailureCountLast7Days);
    }

    [Fact]
    public async Task Handle_UnpricedTemplate_ContributesZeroSpendButStillAppearsInBreakdown()
    {
        var raw = new AeroApiUsageRawData(
            CallsByTemplate: new Dictionary<string, int> { ["/{...}"] = 5 },
            BillableCallsByTemplate: new Dictionary<string, int> { ["/{...}"] = 5 },
            CallsByOutcome: new Dictionary<AeroApiCallOutcome, int> { [AeroApiCallOutcome.Success] = 5 },
            CallsByDay: new Dictionary<DateOnly, int> { [new DateOnly(2026, 8, 1)] = 5 },
            CallsByTemplateForCurrentMonth: new Dictionary<string, int> { ["/{...}"] = 5 },
            Status429CountLast7Days: 0,
            TransportFailureCountLast7Days: 0);
        _repo.GetUsageRollupAsync(
                Arg.Any<DateTime>(), Arg.Any<DateTime>(),
                Arg.Any<DateTime>(), Arg.Any<DateTime>(),
                Arg.Any<DateTime>(), Arg.Any<DateTime>(),
                Arg.Any<CancellationToken>())
            .Returns(raw);
        // Deliberately empty: "/{...}" has no configured price.
        var pricing = new FakePricingOptions { Pricing = new Dictionary<string, decimal>() };
        var handler = new GetAeroApiUsageQueryHandler(_repo, pricing, _throttle);

        var result = await handler.Handle(new GetAeroApiUsageQuery(null, null), CancellationToken.None);

        Assert.Equal(0m, result.EstimatedSpendUsd);
        Assert.Equal(0m, result.BillableSpendUsd);
        Assert.Equal(0m, result.CurrentMonthSpendUsd);
        var breakdown = Assert.Single(result.ByTemplate);
        Assert.Equal("/{...}", breakdown.EndpointTemplate);
        Assert.Equal(5, breakdown.Calls);
        Assert.Equal(0m, breakdown.SpendUsd);
        Assert.Equal(1m, breakdown.Share);
    }

    [Fact]
    public async Task Handle_EmptyRange_ReturnsZeroesRatherThanThrowing()
    {
        var raw = new AeroApiUsageRawData(
            CallsByTemplate: new Dictionary<string, int>(),
            BillableCallsByTemplate: new Dictionary<string, int>(),
            CallsByOutcome: new Dictionary<AeroApiCallOutcome, int>(),
            CallsByDay: new Dictionary<DateOnly, int>(),
            CallsByTemplateForCurrentMonth: new Dictionary<string, int>(),
            Status429CountLast7Days: 0,
            TransportFailureCountLast7Days: 0);
        _repo.GetUsageRollupAsync(
                Arg.Any<DateTime>(), Arg.Any<DateTime>(),
                Arg.Any<DateTime>(), Arg.Any<DateTime>(),
                Arg.Any<DateTime>(), Arg.Any<DateTime>(),
                Arg.Any<CancellationToken>())
            .Returns(raw);
        var pricing = new FakePricingOptions();
        var handler = new GetAeroApiUsageQueryHandler(_repo, pricing, _throttle);

        var result = await handler.Handle(new GetAeroApiUsageQuery(null, null), CancellationToken.None);

        Assert.Equal(0, result.TotalCalls);
        Assert.Equal(0m, result.EstimatedSpendUsd);
        Assert.Equal(0, result.CurrentMonthCalls);
        Assert.Equal(0m, result.CurrentMonthSpendUsd);
        Assert.Empty(result.ByTemplate);
        Assert.Empty(result.ByOutcome);
        Assert.Empty(result.DailyCounts);
        Assert.Equal(0, result.Status429CountLast7Days);
        Assert.Equal(0, result.TransportFailureCountLast7Days);
    }

    [Fact]
    public async Task Handle_NullDates_DefaultsToCurrentUtcMonthThroughNow()
    {
        var raw = new AeroApiUsageRawData(
            new Dictionary<string, int>(),
            new Dictionary<string, int>(),
            new Dictionary<AeroApiCallOutcome, int>(),
            new Dictionary<DateOnly, int>(),
            new Dictionary<string, int>(),
            0, 0);
        _repo.GetUsageRollupAsync(
                Arg.Any<DateTime>(), Arg.Any<DateTime>(),
                Arg.Any<DateTime>(), Arg.Any<DateTime>(),
                Arg.Any<DateTime>(), Arg.Any<DateTime>(),
                Arg.Any<CancellationToken>())
            .Returns(raw);
        var handler = new GetAeroApiUsageQueryHandler(_repo, new FakePricingOptions(), _throttle);
        var before = DateTime.UtcNow;

        var result = await handler.Handle(new GetAeroApiUsageQuery(null, null), CancellationToken.None);

        var after = DateTime.UtcNow;
        Assert.Equal(new DateTime(before.Year, before.Month, 1, 0, 0, 0, DateTimeKind.Utc), result.FromUtc);
        Assert.InRange(result.ToUtc, before, after);
    }

    [Fact]
    public async Task Handle_ExplicitDates_AreUsedVerbatim()
    {
        var raw = new AeroApiUsageRawData(
            new Dictionary<string, int>(),
            new Dictionary<string, int>(),
            new Dictionary<AeroApiCallOutcome, int>(),
            new Dictionary<DateOnly, int>(),
            new Dictionary<string, int>(),
            0, 0);
        _repo.GetUsageRollupAsync(
                Arg.Any<DateTime>(), Arg.Any<DateTime>(),
                Arg.Any<DateTime>(), Arg.Any<DateTime>(),
                Arg.Any<DateTime>(), Arg.Any<DateTime>(),
                Arg.Any<CancellationToken>())
            .Returns(raw);
        var handler = new GetAeroApiUsageQueryHandler(_repo, new FakePricingOptions(), _throttle);
        var from = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var to = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc);

        var result = await handler.Handle(new GetAeroApiUsageQuery(from, to), CancellationToken.None);

        Assert.Equal(from, result.FromUtc);
        Assert.Equal(to, result.ToUtc);
    }

    [Fact]
    public async Task Handle_UnspecifiedKindDates_AreNormalizedToUtc_BeforeReachingTheRepository()
    {
        // What ASP.NET Core query-string binding actually produces for "?fromUtc=2026-01-01" or
        // "?fromUtc=2026-01-01T00:00:00" (no offset/Z suffix) — Kind=Unspecified, not Kind=Utc.
        // CalledUtc maps to Postgres timestamptz, and Npgsql throws at parameter-write time for any
        // DateTime whose Kind isn't Utc, so this must be normalized before it ever reaches the
        // repository — an InMemory-backed integration test cannot see this, since InMemory ignores
        // DateTimeKind entirely.
        var raw = new AeroApiUsageRawData(
            new Dictionary<string, int>(),
            new Dictionary<string, int>(),
            new Dictionary<AeroApiCallOutcome, int>(),
            new Dictionary<DateOnly, int>(),
            new Dictionary<string, int>(),
            0, 0);
        DateTime? capturedFrom = null;
        DateTime? capturedTo = null;
        _repo.GetUsageRollupAsync(
                Arg.Do<DateTime>(d => capturedFrom = d), Arg.Do<DateTime>(d => capturedTo = d),
                Arg.Any<DateTime>(), Arg.Any<DateTime>(),
                Arg.Any<DateTime>(), Arg.Any<DateTime>(),
                Arg.Any<CancellationToken>())
            .Returns(raw);
        var handler = new GetAeroApiUsageQueryHandler(_repo, new FakePricingOptions(), _throttle);
        var from = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Unspecified);
        var to = new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Unspecified);

        var result = await handler.Handle(new GetAeroApiUsageQuery(from, to), CancellationToken.None);

        Assert.NotNull(capturedFrom);
        Assert.NotNull(capturedTo);
        Assert.Equal(DateTimeKind.Utc, capturedFrom!.Value.Kind);
        Assert.Equal(DateTimeKind.Utc, capturedTo!.Value.Kind);
        Assert.Equal(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), capturedFrom.Value);
        Assert.Equal(new DateTime(2026, 1, 2, 0, 0, 0, DateTimeKind.Utc), capturedTo.Value);
        // The result echoes the normalized (not the raw, Unspecified-kind) bounds back too.
        Assert.Equal(DateTimeKind.Utc, result.FromUtc.Kind);
        Assert.Equal(DateTimeKind.Utc, result.ToUtc.Kind);
    }

    [Fact]
    public async Task Handle_CurrentMonthSpend_IsComputedFromItsOwnDictionary_NotTheRangeDictionary()
    {
        // CallsByTemplate (the requested range) and CallsByTemplateForCurrentMonth are deliberately
        // DIFFERENT dictionaries here — a range spanning a month boundary, or lying entirely in a
        // past month, is the common case in practice, not the exception. If BuildResult ever priced
        // CallsByTemplate for the current-month figures too, this test would fail; a fixture where
        // the two dictionaries happen to be identical could not catch that mistake.
        var raw = new AeroApiUsageRawData(
            CallsByTemplate: new Dictionary<string, int> { ["/schedules/{range}"] = 100 },
            BillableCallsByTemplate: new Dictionary<string, int> { ["/schedules/{range}"] = 100 },
            CallsByOutcome: new Dictionary<AeroApiCallOutcome, int> { [AeroApiCallOutcome.Success] = 100 },
            CallsByDay: new Dictionary<DateOnly, int> { [new DateOnly(2026, 1, 15)] = 100 },
            CallsByTemplateForCurrentMonth: new Dictionary<string, int> { ["/flights/{ident}"] = 7 },
            Status429CountLast7Days: 0,
            TransportFailureCountLast7Days: 0);
        _repo.GetUsageRollupAsync(
                Arg.Any<DateTime>(), Arg.Any<DateTime>(),
                Arg.Any<DateTime>(), Arg.Any<DateTime>(),
                Arg.Any<DateTime>(), Arg.Any<DateTime>(),
                Arg.Any<CancellationToken>())
            .Returns(raw);
        var pricing = new FakePricingOptions
        {
            Pricing = new Dictionary<string, decimal>
            {
                ["/schedules/{range}"] = 0.01m,
                ["/flights/{ident}"] = 0.02m,
            },
        };
        var handler = new GetAeroApiUsageQueryHandler(_repo, pricing, _throttle);

        var result = await handler.Handle(new GetAeroApiUsageQuery(null, null), CancellationToken.None);

        Assert.Equal(100, result.TotalCalls);
        Assert.Equal(1.0m, result.EstimatedSpendUsd); // 100 * 0.01
        Assert.Equal(7, result.CurrentMonthCalls);
        Assert.Equal(0.14m, result.CurrentMonthSpendUsd); // 7 * 0.02
        Assert.NotEqual(result.TotalCalls, result.CurrentMonthCalls);
        Assert.NotEqual(result.EstimatedSpendUsd, result.CurrentMonthSpendUsd);
    }

    [Fact]
    public async Task Handle_SurfacesNextSlotUtc_FromTheThrottleObserver()
    {
        // NextSlotUtc is deliberately NOT part of AeroApiUsageRawData or BuildResult's pure
        // arithmetic (issue #293) — it comes from the live throttle gate, attached by Handle. This
        // proves the wiring, not just that BuildResult defaults the field to null.
        var raw = new AeroApiUsageRawData(
            new Dictionary<string, int>(),
            new Dictionary<string, int>(),
            new Dictionary<AeroApiCallOutcome, int>(),
            new Dictionary<DateOnly, int>(),
            new Dictionary<string, int>(),
            0, 0);
        _repo.GetUsageRollupAsync(
                Arg.Any<DateTime>(), Arg.Any<DateTime>(),
                Arg.Any<DateTime>(), Arg.Any<DateTime>(),
                Arg.Any<DateTime>(), Arg.Any<DateTime>(),
                Arg.Any<CancellationToken>())
            .Returns(raw);
        var nextSlotUtc = new DateTimeOffset(2026, 8, 31, 12, 0, 7, TimeSpan.Zero);
        _throttle.NextSlotUtc.Returns(nextSlotUtc);
        var handler = new GetAeroApiUsageQueryHandler(_repo, new FakePricingOptions(), _throttle);

        var result = await handler.Handle(new GetAeroApiUsageQuery(null, null), CancellationToken.None);

        Assert.Equal(nextSlotUtc, result.NextSlotUtc);
    }

    [Fact]
    public async Task Handle_NoReservationYet_SurfacesNullNextSlotUtc()
    {
        var raw = new AeroApiUsageRawData(
            new Dictionary<string, int>(),
            new Dictionary<string, int>(),
            new Dictionary<AeroApiCallOutcome, int>(),
            new Dictionary<DateOnly, int>(),
            new Dictionary<string, int>(),
            0, 0);
        _repo.GetUsageRollupAsync(
                Arg.Any<DateTime>(), Arg.Any<DateTime>(),
                Arg.Any<DateTime>(), Arg.Any<DateTime>(),
                Arg.Any<DateTime>(), Arg.Any<DateTime>(),
                Arg.Any<CancellationToken>())
            .Returns(raw);
        _throttle.NextSlotUtc.Returns((DateTimeOffset?)null);
        var handler = new GetAeroApiUsageQueryHandler(_repo, new FakePricingOptions(), _throttle);

        var result = await handler.Handle(new GetAeroApiUsageQuery(null, null), CancellationToken.None);

        Assert.Null(result.NextSlotUtc);
    }
}
