using FlightAssistant.Application.Interfaces.ExternalServices;
using FlightAssistant.Infrastructure.ExternalServices;
using FlightAssistant.Infrastructure.ExternalServices.AeroDataBox;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace FlightAssistant.Infrastructure.Tests.ExternalServices;

/// <summary>
/// Proves <see cref="FlightSearchProviderSelector"/> routes a search by criteria shape AND by
/// whether AeroDataBox is configured (#422 final review, finding 4 — not "by criteria shape alone":
/// <see cref="SearchAsync_FullRouteNoFlightNumber_AeroDataBoxUnconfigured_FallsBackToAeroApi"/>
/// below is exactly the configuration dimension, in this same file): a full route with no usable
/// flight number goes to AeroDataBox only when it is configured; everything else — including every
/// shape <c>AeroApiFlightSearchProvider</c>'s own guard rejects — goes to AeroAPI, so that guard
/// keeps producing the same error for the same input. Both targets are NSubstitute fakes; every
/// test asserts which one received the call AND that the other received none, since a selector that
/// calls both (or neither) would still let the "answer" assertion alone pass.
/// </summary>
public class FlightSearchProviderSelectorTests
{
    private static readonly DateOnly Date = new(2026, 10, 1);

    private static readonly FlightSearchResult AeroDataBoxRow = new(
        FlightNumber: "TK1028",
        ServiceDate: Date,
        DepartureAirportIata: "IST",
        ArrivalAirportIata: "JFK",
        ScheduledDepartureUtc: new DateTime(2026, 10, 1, 10, 0, 0, DateTimeKind.Utc),
        ScheduledArrivalUtc: new DateTime(2026, 10, 1, 15, 0, 0, DateTimeKind.Utc));

    private static readonly FlightSearchResult AeroApiRow = AeroDataBoxRow with { FlightNumber = "BA123" };

    private readonly IFlightSearchProvider _aeroDataBox = Substitute.For<IFlightSearchProvider>();
    private readonly IFlightSearchProvider _aeroApi = Substitute.For<IFlightSearchProvider>();

    private FlightSearchProviderSelector Selector(bool aeroDataBoxConfigured = true) =>
        new(
            _aeroDataBox,
            _aeroApi,
            Options.Create(new AeroDataBoxOptions { ApiKey = aeroDataBoxConfigured ? "dummy-test-key" : null }),
            NullLogger<FlightSearchProviderSelector>.Instance);

    private async Task AssertRoutesToAeroDataBoxAsync(FlightSearchCriteria criteria)
    {
        _aeroDataBox.SearchAsync(Arg.Any<FlightSearchCriteria>(), Arg.Any<CancellationToken>())
            .Returns(new List<FlightSearchResult> { AeroDataBoxRow });

        var result = await Selector().SearchAsync(criteria);

        Assert.Equal(AeroDataBoxRow, Assert.Single(result));
        await _aeroDataBox.Received(1).SearchAsync(criteria, Arg.Any<CancellationToken>());
        await _aeroApi.DidNotReceive().SearchAsync(Arg.Any<FlightSearchCriteria>(), Arg.Any<CancellationToken>());
    }

    private async Task AssertRoutesToAeroApiAsync(FlightSearchCriteria criteria, bool aeroDataBoxConfigured = true)
    {
        _aeroApi.SearchAsync(Arg.Any<FlightSearchCriteria>(), Arg.Any<CancellationToken>())
            .Returns(new List<FlightSearchResult> { AeroApiRow });

        var result = await Selector(aeroDataBoxConfigured).SearchAsync(criteria);

        Assert.Equal(AeroApiRow, Assert.Single(result));
        await _aeroApi.Received(1).SearchAsync(criteria, Arg.Any<CancellationToken>());
        await _aeroDataBox.DidNotReceive().SearchAsync(Arg.Any<FlightSearchCriteria>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public Task SearchAsync_FullRouteNoFlightNumber_Configured_RoutesToAeroDataBox() =>
        AssertRoutesToAeroDataBoxAsync(new FlightSearchCriteria("IST", "JFK", Date, null, false));

    [Fact]
    public Task SearchAsync_RoutePlusUsableFlightNumber_RoutesToAeroApi() =>
        AssertRoutesToAeroApiAsync(new FlightSearchCriteria("IST", "JFK", Date, "TK1028", false));

    [Fact]
    public Task SearchAsync_FlightNumberOnly_RoutesToAeroApi() =>
        AssertRoutesToAeroApiAsync(new FlightSearchCriteria(null, null, Date, "BA123", false));

    [Fact]
    public Task SearchAsync_DepartureOnly_RoutesToAeroApi() =>
        AssertRoutesToAeroApiAsync(new FlightSearchCriteria("IST", null, Date, null, false));

    [Fact]
    public Task SearchAsync_ArrivalOnly_RoutesToAeroApi() =>
        AssertRoutesToAeroApiAsync(new FlightSearchCriteria(null, "JFK", Date, null, false));

    [Fact]
    public Task SearchAsync_WhitespaceAirports_RoutesToAeroApi() =>
        AssertRoutesToAeroApiAsync(new FlightSearchCriteria("   ", "  ", Date, null, false));

    /// <summary>A 1-character flight number is not "usable" per
    /// <c>AeroApiFlightSearchProvider.HasUsableFlightNumber</c> (needs the 2-character airline
    /// prefix), so the selector must mirror that guard exactly rather than restate its own
    /// threshold.</summary>
    [Fact]
    public Task SearchAsync_FullRoutePlusOneCharacterFlightNumber_RoutesToAeroDataBox_MirroringTheGuardExactly() =>
        AssertRoutesToAeroDataBoxAsync(new FlightSearchCriteria("IST", "JFK", Date, "B", false));

    [Fact]
    public Task SearchAsync_FullRouteNoFlightNumber_AeroDataBoxUnconfigured_FallsBackToAeroApi() =>
        AssertRoutesToAeroApiAsync(new FlightSearchCriteria("IST", "JFK", Date, null, false), aeroDataBoxConfigured: false);

    // ------------------------------------------------------------------
    // Cancellation forwarding (#422 final review, finding 10): the helpers above verify with
    // Arg.Any<CancellationToken>(), which a selector that drops ct entirely would still satisfy —
    // these pin a REAL, non-default token reaching the chosen provider.
    // ------------------------------------------------------------------

    [Fact]
    public async Task SearchAsync_ForwardsTheRealCancellationToken_WhenRoutedToAeroDataBox()
    {
        using var cts = new CancellationTokenSource();
        var criteria = new FlightSearchCriteria("IST", "JFK", Date, null, false);
        _aeroDataBox.SearchAsync(Arg.Any<FlightSearchCriteria>(), Arg.Any<CancellationToken>())
            .Returns(new List<FlightSearchResult> { AeroDataBoxRow });

        await Selector().SearchAsync(criteria, cts.Token);

        await _aeroDataBox.Received(1).SearchAsync(criteria, cts.Token);
    }

    [Fact]
    public async Task SearchAsync_ForwardsTheRealCancellationToken_WhenRoutedToAeroApi()
    {
        using var cts = new CancellationTokenSource();
        var criteria = new FlightSearchCriteria(null, null, Date, "BA123", false);
        _aeroApi.SearchAsync(Arg.Any<FlightSearchCriteria>(), Arg.Any<CancellationToken>())
            .Returns(new List<FlightSearchResult> { AeroApiRow });

        await Selector().SearchAsync(criteria, cts.Token);

        await _aeroApi.Received(1).SearchAsync(criteria, cts.Token);
    }
}
