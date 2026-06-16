using FlightAssistant.API.HostedServices;

namespace FlightAssistant.API.Tests.HostedServices;

/// <summary>
/// Unit tests for <see cref="FlightStatusSyncBackgroundService.IsActiveForFastPoll"/>.
///
/// Regression test for #242: the fast-poll (2-minute) window used to be triggered by
/// <c>(ScheduledDepartureUtc - now).TotalHours &lt;= 4</c>, which is also true for negative
/// values — so a same-day flight that had already departed/landed kept the service in the
/// fast-poll cycle indefinitely. The predicate now bounds the window to the genuinely-active
/// period: from 4 hours before scheduled departure THROUGH arrival.
/// </summary>
public class FlightStatusSyncBackgroundServiceTests
{
    private static readonly DateTime Now = new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void IsActiveForFastPoll_WellBeforeDeparture_ReturnsFalse()
    {
        // Departs in 6h, arrives in 8h — outside the 4h window.
        var result = FlightStatusSyncBackgroundService.IsActiveForFastPoll(
            scheduledDepartureUtc: Now.AddHours(6),
            scheduledArrivalUtc: Now.AddHours(8),
            actualArrivalUtc: null,
            now: Now);

        Assert.False(result);
    }

    [Fact]
    public void IsActiveForFastPoll_WithinFourHourWindow_ReturnsTrue()
    {
        // Departs in 2h, arrives in 4h.
        var result = FlightStatusSyncBackgroundService.IsActiveForFastPoll(
            scheduledDepartureUtc: Now.AddHours(2),
            scheduledArrivalUtc: Now.AddHours(4),
            actualArrivalUtc: null,
            now: Now);

        Assert.True(result);
    }

    [Fact]
    public void IsActiveForFastPoll_InFlight_ReturnsTrue()
    {
        // Departed 1h ago, arrives in 2h.
        var result = FlightStatusSyncBackgroundService.IsActiveForFastPoll(
            scheduledDepartureUtc: Now.AddHours(-1),
            scheduledArrivalUtc: Now.AddHours(2),
            actualArrivalUtc: null,
            now: Now);

        Assert.True(result);
    }

    [Fact]
    public void IsActiveForFastPoll_AlreadyArrivedBySchedule_ReturnsFalse()
    {
        // Departed 5h ago, scheduled arrival 3h ago, no actual arrival recorded.
        var result = FlightStatusSyncBackgroundService.IsActiveForFastPoll(
            scheduledDepartureUtc: Now.AddHours(-5),
            scheduledArrivalUtc: Now.AddHours(-3),
            actualArrivalUtc: null,
            now: Now);

        Assert.False(result);
    }

    [Fact]
    public void IsActiveForFastPoll_AlreadyArrivedByActual_ReturnsFalse()
    {
        // Actual arrival 1h ago even though scheduled arrival is still in the future.
        var result = FlightStatusSyncBackgroundService.IsActiveForFastPoll(
            scheduledDepartureUtc: Now.AddHours(-2),
            scheduledArrivalUtc: Now.AddHours(1),
            actualArrivalUtc: Now.AddHours(-1),
            now: Now);

        Assert.False(result);
    }

    [Fact]
    public void IsActiveForFastPoll_DeparturesExactlyFourHoursAway_ReturnsTrue()
    {
        // Boundary: departs in exactly 4h, arrives later.
        var result = FlightStatusSyncBackgroundService.IsActiveForFastPoll(
            scheduledDepartureUtc: Now.AddHours(4),
            scheduledArrivalUtc: Now.AddHours(6),
            actualArrivalUtc: null,
            now: Now);

        Assert.True(result);
    }
}
