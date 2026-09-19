using FlightAssistant.Application.Interfaces.ExternalServices;
using FlightAssistant.Application.Interfaces.Providers;
using FlightAssistant.Application.Providers;
using FlightAssistant.Infrastructure.ExternalServices;
using FlightAssistant.Infrastructure.ExternalServices.AeroDataBox;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace FlightAssistant.Infrastructure.Tests.ExternalServices;

/// <summary>
/// Proves <see cref="FlightSearchProviderSelector"/> dispatches to whichever provider
/// <see cref="IFlightSearchRouter"/> names, forwarding the same criteria and cancellation token, and
/// leaves the other provider untouched. Before #531 the selector decided criteria shape AND
/// AeroDataBox configuration itself; that decision now lives in <see cref="IFlightSearchRouter"/>
/// (proven separately by <see cref="FlightSearchRouterTests"/> below), so every test here drives a
/// substitute router rather than real configuration. Each test keeps its original per-shape name and
/// criteria so the scenario it documents — including
/// <see cref="SearchAsync_FullRouteNoFlightNumber_AeroDataBoxUnconfigured_FallsBackToAeroApi"/>'s
/// configuration-ceiling case — is still legible, even though the router substitute, not the
/// criteria, is what actually supplies the selector's answer now. Both targets are NSubstitute
/// fakes; every test asserts which one received the call AND that the other received none, since a
/// selector that calls both (or neither) would still let the "answer" assertion alone pass.
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
    private readonly IFlightSearchRouter _router = Substitute.For<IFlightSearchRouter>();

    // #531: the selector no longer decides configuration or criteria shape itself — it only
    // dispatches to whatever IFlightSearchRouter resolves. That decision now lives in
    // FlightSearchRouter (covered separately by FlightSearchRouterTests below), so this helper no
    // longer needs an AeroDataBoxOptions of its own.
    private FlightSearchProviderSelector Selector() =>
        new(
            _aeroDataBox,
            _aeroApi,
            _router,
            NullLogger<FlightSearchProviderSelector>.Instance);

    private async Task AssertRoutesToAeroDataBoxAsync(FlightSearchCriteria criteria)
    {
        _router.ResolveAsync(Arg.Any<FlightSearchCriteria>(), Arg.Any<CancellationToken>())
            .Returns(ProviderJobs.AeroDataBox);
        _aeroDataBox.SearchAsync(Arg.Any<FlightSearchCriteria>(), Arg.Any<CancellationToken>())
            .Returns(new List<FlightSearchResult> { AeroDataBoxRow });

        var result = await Selector().SearchAsync(criteria);

        Assert.Equal(AeroDataBoxRow, Assert.Single(result));
        await _aeroDataBox.Received(1).SearchAsync(criteria, Arg.Any<CancellationToken>());
        await _aeroApi.DidNotReceive().SearchAsync(Arg.Any<FlightSearchCriteria>(), Arg.Any<CancellationToken>());
    }

    // aeroDataBoxConfigured no longer changes the selector's own behaviour: #531 moved the
    // configuration ceiling into IFlightSearchRouter (ResolveAsync_ConfigCeiling_OverridesTheSwitch
    // in FlightSearchRouterTests below covers it directly now). Kept as a parameter purely so
    // SearchAsync_FullRouteNoFlightNumber_AeroDataBoxUnconfigured_FallsBackToAeroApi's call site
    // below still documents that scenario by name; the router substitute is what actually decides
    // the selector's outcome here.
    private async Task AssertRoutesToAeroApiAsync(FlightSearchCriteria criteria, bool aeroDataBoxConfigured = true)
    {
        _router.ResolveAsync(Arg.Any<FlightSearchCriteria>(), Arg.Any<CancellationToken>())
            .Returns(ProviderJobs.AeroApi);
        _aeroApi.SearchAsync(Arg.Any<FlightSearchCriteria>(), Arg.Any<CancellationToken>())
            .Returns(new List<FlightSearchResult> { AeroApiRow });

        var result = await Selector().SearchAsync(criteria);

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
        _router.ResolveAsync(Arg.Any<FlightSearchCriteria>(), Arg.Any<CancellationToken>())
            .Returns(ProviderJobs.AeroDataBox);
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
        _router.ResolveAsync(Arg.Any<FlightSearchCriteria>(), Arg.Any<CancellationToken>())
            .Returns(ProviderJobs.AeroApi);
        _aeroApi.SearchAsync(Arg.Any<FlightSearchCriteria>(), Arg.Any<CancellationToken>())
            .Returns(new List<FlightSearchResult> { AeroApiRow });

        await Selector().SearchAsync(criteria, cts.Token);

        await _aeroApi.Received(1).SearchAsync(criteria, cts.Token);
    }
}

/// <summary>
/// Proves <see cref="FlightSearchRouter"/> combines the three inputs correctly: the criteria-shape
/// rule (#422, unchanged and not switchable), the AeroDataBox configuration ceiling, and the
/// admin-controlled routing switch (#531) read through <see cref="IProviderRouting"/>.
/// </summary>
public class FlightSearchRouterTests
{
    private static FlightSearchRouter Build(string chosen, bool aeroDataBoxConfigured = true)
    {
        var routing = Substitute.For<IProviderRouting>();
        routing.GetProvidersAsync(ProviderJobs.FlightSearchRoute, Arg.Any<CancellationToken>())
               .Returns(new[] { chosen });

        var options = Options.Create(new AeroDataBoxOptions
        {
            ApiKey = aeroDataBoxConfigured ? "key" : null,
        });

        return new FlightSearchRouter(routing, options, NullLogger<FlightSearchRouter>.Instance);
    }

    private static FlightSearchCriteria Route() => new("SOF", "IST", new DateOnly(2026, 9, 20), null);

    [Fact]
    public async Task ResolveAsync_RouteShapeAndSwitchSaysAeroDataBox_ReturnsAeroDataBox()
    {
        var router = Build(ProviderJobs.AeroDataBox);
        Assert.Equal(ProviderJobs.AeroDataBox, await router.ResolveAsync(Route(), default));
    }

    [Fact]
    public async Task ResolveAsync_RouteShapeAndSwitchSaysAeroApi_ReturnsAeroApi()
    {
        var router = Build(ProviderJobs.AeroApi);
        Assert.Equal(ProviderJobs.AeroApi, await router.ResolveAsync(Route(), default));
    }

    [Fact]
    public async Task ResolveAsync_ConfigCeiling_OverridesTheSwitch()
    {
        // AeroDataBox:ApiKey unset is a hard ceiling: a switch must never select a provider the
        // deployment cannot authenticate to.
        var router = Build(ProviderJobs.AeroDataBox, aeroDataBoxConfigured: false);
        Assert.Equal(ProviderJobs.AeroApi, await router.ResolveAsync(Route(), default));
    }

    [Fact]
    public async Task ResolveAsync_FlightNumberShape_StaysOnAeroApiWhateverTheSwitchSays()
    {
        // #422: AeroDataBox resolves a marketing number to its operator (6E4120 -> TK 1028), so
        // this is a correctness rule, not a cost preference, and the switch cannot reach it.
        var router = Build(ProviderJobs.AeroDataBox);
        var criteria = new FlightSearchCriteria("SOF", "IST", new DateOnly(2026, 9, 20), "TK1028");

        Assert.Equal(ProviderJobs.AeroApi, await router.ResolveAsync(criteria, default));
    }

    [Fact]
    public async Task ResolveAsync_SingleAirport_StaysOnAeroApiSoItsOwnGuardStillRefuses()
    {
        var router = Build(ProviderJobs.AeroDataBox);
        var criteria = new FlightSearchCriteria("SOF", null, new DateOnly(2026, 9, 20), null);

        Assert.Equal(ProviderJobs.AeroApi, await router.ResolveAsync(criteria, default));
    }
}
