using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Application.Queries.Admin.Stats;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;

namespace FlightAssistant.Application.Tests.Queries.Admin.Stats;

/// <summary>
/// The dashboard's stats (#336). What carries the risk is the window: it must be aligned to whole
/// buckets, so "new in period" always equals the sum of its series and the panel can link the
/// number to the data browser with the same <c>fromUtc</c>. And each added row must land in exactly
/// one bucket.
/// </summary>
public class GetAdminStatsQueryTests
{
    // 16:45:30 on 14 Sep: mid-hour and mid-day, so a rolling window would show up as wrong edges.
    private static readonly DateTimeOffset Now = new(2026, 9, 14, 16, 45, 30, TimeSpan.Zero);

    private static DateTime Utc(int month, int day, int hour = 0, int minute = 0, int second = 0) =>
        new(2026, month, day, hour, minute, second, DateTimeKind.Utc);

    private readonly IAdminStatsRepository _repository = Substitute.For<IAdminStatsRepository>();
    private IReadOnlyList<AdminEntityCounts> _counts = [new("travelers", "CreatedUtc", 0, null, [])];
    private readonly GetAdminStatsQueryHandler _handler;

    public GetAdminStatsQueryTests()
    {
        _repository
            .GetCountsAsync(Arg.Any<DateTime>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(_ => _counts);
        _handler = new GetAdminStatsQueryHandler(_repository, new FakeTimeProvider(Now));
    }

    // ── Validation ────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(null)]
    [InlineData("24h")]
    [InlineData("7d")]
    [InlineData("30d")]
    [InlineData("90d")]
    public void Validator_AcceptsTheFourPeriods_AndNoPeriod(string? period)
    {
        var result = new GetAdminStatsQueryValidator().Validate(new GetAdminStatsQuery(period));

        Assert.True(result.IsValid);
    }

    [Theory]
    [InlineData("1y")]
    [InlineData("7D")]
    [InlineData("7")]
    [InlineData("")]
    public void Validator_RejectsAnyOtherPeriod_UnderThePeriodKey(string period)
    {
        var result = new GetAdminStatsQueryValidator().Validate(new GetAdminStatsQuery(period));

        var error = Assert.Single(result.Errors);
        Assert.Equal("period", error.PropertyName);
        Assert.Contains("24h, 7d, 30d, 90d", error.ErrorMessage);
    }

    // ── The window ────────────────────────────────────────────────────────────

    [Fact]
    public async Task Handle_DefaultsToSevenWholeUtcDays_EndingWithToday()
    {
        var result = await _handler.Handle(new GetAdminStatsQuery(), CancellationToken.None);

        Assert.Equal("7d", result.Period);
        Assert.Equal("day", result.Bucket);
        Assert.Equal(new DateTimeOffset(2026, 9, 8, 0, 0, 0, TimeSpan.Zero), result.FromUtc);
        Assert.Equal(new DateTimeOffset(2026, 9, 15, 0, 0, 0, TimeSpan.Zero), result.ToUtc);
        await _repository.Received(1).GetCountsAsync(
            Arg.Is<DateTime>(d => d == Utc(9, 8) && d.Kind == DateTimeKind.Utc),
            Arg.Is<DateTime>(d => d == Utc(9, 15) && d.Kind == DateTimeKind.Utc),
            Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("24h", "2026-09-13T17:00:00Z", "2026-09-14T17:00:00Z", "hour", 24)]
    [InlineData("7d", "2026-09-08T00:00:00Z", "2026-09-15T00:00:00Z", "day", 7)]
    [InlineData("30d", "2026-08-16T00:00:00Z", "2026-09-15T00:00:00Z", "day", 30)]
    [InlineData("90d", "2026-06-17T00:00:00Z", "2026-09-15T00:00:00Z", "day", 90)]
    public async Task Handle_AlignsEachPeriod_ToWholeBuckets_EndingWithTheCurrentOne(
        string period, string from, string to, string bucket, int buckets)
    {
        var result = await _handler.Handle(new GetAdminStatsQuery(period), CancellationToken.None);

        Assert.Equal(DateTimeOffset.Parse(from), result.FromUtc);
        Assert.Equal(DateTimeOffset.Parse(to), result.ToUtc);
        Assert.Equal(bucket, result.Bucket);
        Assert.Equal(buckets, Assert.Single(result.Entities).Series.Count);
    }

    // ── Buckets ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Handle_PutsEachAddedRowInItsDay_AndNewInPeriodIsTheSeriesSum()
    {
        _counts = [new("airlines", "FirstSeenAt", 12, 5,
            [Utc(9, 8), Utc(9, 8, 23, 59, 59), Utc(9, 9), Utc(9, 14, 16, 45)])];

        var result = await _handler.Handle(new GetAdminStatsQuery("7d"), CancellationToken.None);

        var airlines = Assert.Single(result.Entities);
        Assert.Equal([2, 1, 0, 0, 0, 0, 1], airlines.Series);
        Assert.Equal(4, airlines.NewInPeriod);
    }

    [Fact]
    public async Task Handle_24h_PutsEachAddedRowInItsHour()
    {
        _counts = [new("travelers", "CreatedUtc", 3, null,
            [Utc(9, 13, 17), Utc(9, 14, 16), Utc(9, 14, 16, 59, 59)])];

        var result = await _handler.Handle(new GetAdminStatsQuery("24h"), CancellationToken.None);

        var series = Assert.Single(result.Entities).Series;
        Assert.Equal(1, series[0]);
        Assert.Equal(2, series[23]);
        Assert.Equal(3, series.Sum());
    }

    [Fact]
    public async Task Handle_IgnoresAddedTimesOutsideTheWindow()
    {
        _counts = [new("travelers", "CreatedUtc", 2, null, [Utc(9, 7, 23, 59, 59), Utc(9, 15)])];

        var result = await _handler.Handle(new GetAdminStatsQuery("7d"), CancellationToken.None);

        var travelers = Assert.Single(result.Entities);
        Assert.All(travelers.Series, count => Assert.Equal(0, count));
        Assert.Equal(0, travelers.NewInPeriod);
    }

    // ── Totals and verification ───────────────────────────────────────────────

    [Fact]
    public async Task Handle_UnverifiedIsTotalMinusVerified_AndNullWhereTheCardIsNotVerifiable()
    {
        _counts =
        [
            new("travelers", "CreatedUtc", 4, null, []),
            new("airlines", "FirstSeenAt", 10, 7, []),
        ];

        var result = await _handler.Handle(new GetAdminStatsQuery(), CancellationToken.None);

        Assert.Equal(["travelers", "airlines"], result.Entities.Select(e => e.Key));
        var (travelers, airlines) = (result.Entities[0], result.Entities[1]);
        Assert.Equal((4, (int?)null, (int?)null, "CreatedUtc"), (travelers.Total, travelers.Verified, travelers.Unverified, travelers.DateColumn));
        Assert.Equal((10, (int?)7, (int?)3, "FirstSeenAt"), (airlines.Total, airlines.Verified, airlines.Unverified, airlines.DateColumn));
    }
}
