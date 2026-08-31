using FlightAssistant.Application.Interfaces.Configuration;
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

    [Fact]
    public async Task Handle_RollsUpCallsAcrossTemplatesAndOutcomes_WithPricedSpendAndShare()
    {
        var raw = new AeroApiUsageRawData(
            CallsByTemplate: new Dictionary<string, int>
            {
                ["/schedules/{range}"] = 30,
                ["/flights/{ident}"] = 10,
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
            Status502CountLast7Days: 1);
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
        var handler = new GetAeroApiUsageQueryHandler(_repo, pricing);

        var result = await handler.Handle(new GetAeroApiUsageQuery(null, null), CancellationToken.None);

        Assert.Equal(40, result.TotalCalls);
        // 30 * 0.01 + 10 * 0.02 = 0.5
        Assert.Equal(0.5m, result.EstimatedSpendUsd);
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
        Assert.Equal(1, result.Status502CountLast7Days);
    }

    [Fact]
    public async Task Handle_UnpricedTemplate_ContributesZeroSpendButStillAppearsInBreakdown()
    {
        var raw = new AeroApiUsageRawData(
            CallsByTemplate: new Dictionary<string, int> { ["/{...}"] = 5 },
            CallsByOutcome: new Dictionary<AeroApiCallOutcome, int> { [AeroApiCallOutcome.Success] = 5 },
            CallsByDay: new Dictionary<DateOnly, int> { [new DateOnly(2026, 8, 1)] = 5 },
            CallsByTemplateForCurrentMonth: new Dictionary<string, int> { ["/{...}"] = 5 },
            Status429CountLast7Days: 0,
            Status502CountLast7Days: 0);
        _repo.GetUsageRollupAsync(
                Arg.Any<DateTime>(), Arg.Any<DateTime>(),
                Arg.Any<DateTime>(), Arg.Any<DateTime>(),
                Arg.Any<DateTime>(), Arg.Any<DateTime>(),
                Arg.Any<CancellationToken>())
            .Returns(raw);
        // Deliberately empty: "/{...}" has no configured price.
        var pricing = new FakePricingOptions { Pricing = new Dictionary<string, decimal>() };
        var handler = new GetAeroApiUsageQueryHandler(_repo, pricing);

        var result = await handler.Handle(new GetAeroApiUsageQuery(null, null), CancellationToken.None);

        Assert.Equal(0m, result.EstimatedSpendUsd);
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
            CallsByOutcome: new Dictionary<AeroApiCallOutcome, int>(),
            CallsByDay: new Dictionary<DateOnly, int>(),
            CallsByTemplateForCurrentMonth: new Dictionary<string, int>(),
            Status429CountLast7Days: 0,
            Status502CountLast7Days: 0);
        _repo.GetUsageRollupAsync(
                Arg.Any<DateTime>(), Arg.Any<DateTime>(),
                Arg.Any<DateTime>(), Arg.Any<DateTime>(),
                Arg.Any<DateTime>(), Arg.Any<DateTime>(),
                Arg.Any<CancellationToken>())
            .Returns(raw);
        var pricing = new FakePricingOptions();
        var handler = new GetAeroApiUsageQueryHandler(_repo, pricing);

        var result = await handler.Handle(new GetAeroApiUsageQuery(null, null), CancellationToken.None);

        Assert.Equal(0, result.TotalCalls);
        Assert.Equal(0m, result.EstimatedSpendUsd);
        Assert.Equal(0, result.CurrentMonthCalls);
        Assert.Equal(0m, result.CurrentMonthSpendUsd);
        Assert.Empty(result.ByTemplate);
        Assert.Empty(result.ByOutcome);
        Assert.Empty(result.DailyCounts);
        Assert.Equal(0, result.Status429CountLast7Days);
        Assert.Equal(0, result.Status502CountLast7Days);
    }

    [Fact]
    public async Task Handle_NullDates_DefaultsToCurrentUtcMonthThroughNow()
    {
        var raw = new AeroApiUsageRawData(
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
        var handler = new GetAeroApiUsageQueryHandler(_repo, new FakePricingOptions());
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
        var handler = new GetAeroApiUsageQueryHandler(_repo, new FakePricingOptions());
        var from = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var to = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc);

        var result = await handler.Handle(new GetAeroApiUsageQuery(from, to), CancellationToken.None);

        Assert.Equal(from, result.FromUtc);
        Assert.Equal(to, result.ToUtc);
    }
}
