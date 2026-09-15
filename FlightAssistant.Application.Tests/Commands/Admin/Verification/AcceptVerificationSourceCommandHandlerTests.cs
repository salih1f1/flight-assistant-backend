using FlightAssistant.Application.Commands.Admin.Verification;
using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Application.Verification;
using FlightAssistant.Domain.Common.Exceptions;
using FlightAssistant.Domain.ReferenceData.Entities;
using FlightAssistant.Domain.Verification.Entities;
using FlightAssistant.Domain.Verification.Enums;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using NSubstitute.ReturnsExtensions;

namespace FlightAssistant.Application.Tests.Commands.Admin.Verification;

/// <summary>
/// <see cref="AcceptVerificationSourceCommandHandler"/> (#378, spec §4.4). Every request is a real
/// <see cref="VerificationRequest"/> completed as <c>Mismatch</c> with the JSON the real
/// <see cref="VerificationComparer"/> and <see cref="VerificationJson"/> produce, and every entity is
/// real, so the assertions read the entity's state rather than calls made on it.
/// </summary>
public class AcceptVerificationSourceCommandHandlerTests
{
    private static readonly DateTime CheckedAt = new(2026, 9, 14, 8, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Now = new(2026, 9, 15, 12, 0, 0, DateTimeKind.Utc);
    private const long RequestId = 7;
    private const int EntityId = 42;
    private const string Actor = "admin-oid-1";
    private const string OurAirports = "ourairports";
    private const string Mledoze = "mledoze";

    private readonly IVerificationRequestRepository _requests = Substitute.For<IVerificationRequestRepository>();
    private readonly IAirportRepository _airports = Substitute.For<IAirportRepository>();
    private readonly IAirlineRepository _airlines = Substitute.For<IAirlineRepository>();
    private readonly ICountryRepository _countries = Substitute.For<ICountryRepository>();
    private readonly FakeTimeProvider _time = new(new DateTimeOffset(Now));

    private Task Accept(string source = OurAirports, string verifiedBy = Actor) =>
        new AcceptVerificationSourceCommandHandler(_requests, _airports, _airlines, _countries, _time)
            .Handle(new AcceptVerificationSourceCommand(RequestId, source, verifiedBy), CancellationToken.None);

    private async Task<DomainException> AcceptFails(int statusCode, string source = OurAirports)
    {
        var ex = await Assert.ThrowsAsync<DomainException>(() => Accept(source));
        Assert.Equal(statusCode, ex.StatusCode);
        return ex;
    }

    // ── Fixtures ────────────────────────────────────────────────────────────

    private static TEntity WithId<TEntity>(TEntity entity, int id)
    {
        typeof(TEntity).GetProperty("Id")!.SetValue(entity, id);
        return entity;
    }

    private static Airport Heathrow(int id = EntityId) =>
        WithId(Airport.Create("LHR", "EGLL", "London Heathrow", "London", "GB", 51.4706, -0.461941, 83), id);

    private static Country UnitedKingdom() =>
        Country.Create("GB", "United Kingdom", "GBR", 826, "Europe", "Northern Europe", isEU: false, isSchengen: false, isEEA: false);

    private Airport LoadedAirport(Airport? airport = null)
    {
        airport ??= Heathrow();
        _airports.GetByIdAsync(EntityId, Arg.Any<CancellationToken>()).Returns(airport);
        return airport;
    }

    private Country LoadedCountry(Country? country = null)
    {
        country ??= UnitedKingdom();
        _countries.FindByCodeAsync(country.Code, Arg.Any<CancellationToken>()).Returns(country);
        return country;
    }

    private static Dictionary<string, string?> With(
        IReadOnlyDictionary<string, string?> record, params (string Column, string? Value)[] overrides)
    {
        var copy = new Dictionary<string, string?>(record, StringComparer.Ordinal);
        foreach (var (column, value) in overrides)
            copy[column] = value;
        return copy;
    }

    /// <summary>What the job stores: the real comparer over our values at check time and each
    /// source's record, saved on the request by <c>MarkMismatch</c>.</summary>
    private VerificationRequest CheckedAsMismatch(
        VerificationEntityType entityType,
        string entityKey,
        IReadOnlyDictionary<string, string?> oursAtCheck,
        params (string Source, IReadOnlyDictionary<string, string?> Record)[] sources)
    {
        var comparison = VerificationComparer.Compare(
            entityType,
            oursAtCheck,
            sources.Select(s => new VerificationSourceResult(s.Source, VerificationSourceLookupResult.Found(s.Record))).ToList());
        Assert.Equal(VerificationComparisonOutcome.Mismatch, comparison.Outcome);

        var request = VerificationRequest.Create(
            entityType, entityKey, sources.Select(s => s.Source), VerificationTrigger.Admin, "admin-oid-0", 0m, CheckedAt.AddMinutes(-1));
        request.MarkMismatch(
            VerificationJson.SerializeDifferences(comparison.Differences),
            VerificationJson.SerializeSourceValues(comparison.SourceValues),
            CheckedAt);

        _requests.GetByIdAsync(RequestId, Arg.Any<CancellationToken>()).Returns(request);
        return request;
    }

    /// <summary>Heathrow checked against OurAirports, which returned Heathrow with <paramref name="sourceOverrides"/>.</summary>
    private VerificationRequest AirportMismatch(params (string Column, string? Value)[] sourceOverrides)
    {
        var ours = VerificationCheckedColumns.Project(Heathrow());
        return CheckedAsMismatch(VerificationEntityType.Airport, "42", ours, (OurAirports, With(ours, sourceOverrides)));
    }

    /// <summary>The United Kingdom checked against mledoze, which returned it with <paramref name="sourceOverrides"/>.</summary>
    private VerificationRequest CountryMismatch(params (string Column, string? Value)[] sourceOverrides)
    {
        var ours = VerificationCheckedColumns.Project(UnitedKingdom());
        return CheckedAsMismatch(VerificationEntityType.Country, "GB", ours, (Mledoze, With(ours, sourceOverrides)));
    }

    private async Task AssertNothingSaved()
    {
        await _airports.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
        await _airlines.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
        await _countries.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
        await _requests.DidNotReceive().SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    private static void AssertUnchanged(Airport airport)
    {
        Assert.Equal(VerificationCheckedColumns.Project(Heathrow()), VerificationCheckedColumns.Project(airport));
        Assert.False(airport.IsVerified);
    }

    private static void AssertUnchanged(Country country)
    {
        Assert.Equal(VerificationCheckedColumns.Project(UnitedKingdom()), VerificationCheckedColumns.Project(country));
        Assert.False(country.IsVerified);
    }

    // ── Which request, which source ─────────────────────────────────────────

    [Fact]
    public async Task Handle_TheRequestDoesNotExist_Is404()
    {
        _requests.GetByIdAsync(RequestId, Arg.Any<CancellationToken>()).ReturnsNull();

        await AcceptFails(404);

        await AssertNothingSaved();
    }

    [Theory]
    [InlineData(VerificationRequestStatus.Pending)]
    [InlineData(VerificationRequestStatus.Verified)]
    [InlineData(VerificationRequestStatus.NotFound)]
    [InlineData(VerificationRequestStatus.Error)]
    public async Task Handle_TheRequestIsNotMismatch_Is409(VerificationRequestStatus status)
    {
        var request = VerificationRequest.Create(
            VerificationEntityType.Airport, "42", new[] { OurAirports }, VerificationTrigger.Admin, "admin-oid-0", 0m, CheckedAt.AddMinutes(-1));
        switch (status)
        {
            case VerificationRequestStatus.Verified: request.MarkVerified(CheckedAt); break;
            case VerificationRequestStatus.NotFound: request.MarkNotFound("{}", CheckedAt); break;
            case VerificationRequestStatus.Error: request.MarkError("Entity no longer exists", CheckedAt); break;
        }
        _requests.GetByIdAsync(RequestId, Arg.Any<CancellationToken>()).Returns(request);
        var airport = LoadedAirport();

        await AcceptFails(409);

        Assert.Equal(status, request.Status);
        AssertUnchanged(airport);
        await AssertNothingSaved();
    }

    [Fact]
    public async Task Handle_TheSourceIsNotOneOfTheRequestsSources_Is400()
    {
        AirportMismatch((nameof(Airport.City), "Hounslow"));
        var airport = LoadedAirport();

        await AcceptFails(400, source: Mledoze);

        AssertUnchanged(airport);
        await AssertNothingSaved();
    }

    [Fact]
    public async Task Handle_TheSourceHasNoStoredValues_Is400()
    {
        // Written by hand: the comparer stores every source that answered, so this needs source_values
        // written some other way.
        var ours = VerificationCheckedColumns.Project(Heathrow());
        var request = VerificationRequest.Create(
            VerificationEntityType.Airport, "42", new[] { OurAirports, "other" }, VerificationTrigger.Admin, "admin-oid-0", 0m, CheckedAt.AddMinutes(-1));
        request.MarkMismatch(
            VerificationJson.SerializeDifferences(new[] { new VerificationDifference(nameof(Airport.City), "London", "Hounslow", "other") }),
            VerificationJson.SerializeSourceValues(new Dictionary<string, IReadOnlyDictionary<string, string?>> { [OurAirports] = ours }),
            CheckedAt);
        _requests.GetByIdAsync(RequestId, Arg.Any<CancellationToken>()).Returns(request);
        var airport = LoadedAirport();

        await AcceptFails(400, source: "other");

        AssertUnchanged(airport);
        await AssertNothingSaved();
    }

    [Fact]
    public async Task Handle_TheAirportNoLongerExists_Is404()
    {
        AirportMismatch((nameof(Airport.City), "Hounslow"));
        _airports.GetByIdAsync(EntityId, Arg.Any<CancellationToken>()).ReturnsNull();

        await AcceptFails(404);

        await AssertNothingSaved();
    }

    [Fact]
    public async Task Handle_TheCountryNoLongerExists_Is404()
    {
        CountryMismatch((nameof(Country.Name), "Britain"));
        _countries.FindByCodeAsync("GB", Arg.Any<CancellationToken>()).ReturnsNull();

        await AcceptFails(404, source: Mledoze);

        await AssertNothingSaved();
    }

    // ── The row changed since the check ─────────────────────────────────────

    [Fact]
    public async Task Handle_ADifferingColumnChangedSinceTheCheck_Is409_AndWritesNothing()
    {
        var request = AirportMismatch((nameof(Airport.City), "Hounslow"));
        var airport = LoadedAirport();
        airport.Update(airport.Iata, airport.Icao, airport.Name, "Greater London", airport.CountryCode,
            airport.Latitude, airport.Longitude, airport.ElevationFt);

        var ex = await AcceptFails(409);

        Assert.Contains("City has changed since the check", ex.Message);
        Assert.Equal("Greater London", airport.City);
        Assert.False(airport.IsVerified);
        Assert.Equal(VerificationRequestStatus.Mismatch, request.Status);
        await AssertNothingSaved();
    }

    [Fact]
    public async Task Handle_ADifferingColumnThatOnlyGainedAnAccent_HasNotChanged()
    {
        var atCheck = Airport.Create("IST", "LTFM", "Istanbul Airport", "Istanbul", "TR", 41.2753, 28.7519, 325);
        var ours = VerificationCheckedColumns.Project(atCheck);
        CheckedAsMismatch(VerificationEntityType.Airport, "42", ours,
            (OurAirports, With(ours, (nameof(Airport.Name), "Istanbul Grand Airport"))));
        var airport = LoadedAirport(WithId(
            Airport.Create("IST", "LTFM", "İstanbul Airport", "Istanbul", "TR", 41.2753, 28.7519, 325), EntityId));

        await Accept();

        Assert.Equal("Istanbul Grand Airport", airport.Name);
        Assert.True(airport.IsVerified);
    }

    [Fact]
    public async Task Handle_ADifferingNumberThatMovedWithinTheChecksTolerance_HasNotChanged()
    {
        AirportMismatch((nameof(Airport.Latitude), "51.5"), (nameof(Airport.ElevationFt), "200"));
        var airport = LoadedAirport();
        // 0.005 degrees and 37 ft from what the check saw: the same values by the check's rule.
        airport.Update(airport.Iata, airport.Icao, airport.Name, airport.City, airport.CountryCode,
            51.4756, airport.Longitude, 120);

        await Accept();

        Assert.Equal(51.5, airport.Latitude);
        Assert.Equal(200, airport.ElevationFt);
    }

    [Fact]
    public async Task Handle_ColumnsOnlyAnotherSourceDisagreedOn_AreNeitherCheckedNorWritten()
    {
        var ours = VerificationCheckedColumns.Project(Heathrow());
        CheckedAsMismatch(VerificationEntityType.Airport, "42", ours,
            (OurAirports, With(ours, (nameof(Airport.City), "Hounslow"))),
            ("other", With(ours, (nameof(Airport.Name), "Heathrow Airport"), (nameof(Airport.ElevationFt), "999"))));
        var airport = LoadedAirport();
        // Name, which only "other" disagreed on, has changed since the check.
        airport.Update(airport.Iata, airport.Icao, "LHR Heathrow", airport.City, airport.CountryCode,
            airport.Latitude, airport.Longitude, airport.ElevationFt);

        await Accept();

        Assert.Equal("Hounslow", airport.City);
        Assert.Equal("LHR Heathrow", airport.Name);
        Assert.Equal(83, airport.ElevationFt);
        Assert.Equal(OurAirports, airport.VerificationSource);
    }

    [Fact]
    public async Task Handle_TheChosenSourceMatchedEveryColumn_OnlyVerifies()
    {
        var ours = VerificationCheckedColumns.Project(Heathrow());
        CheckedAsMismatch(VerificationEntityType.Airport, "42", ours,
            (OurAirports, ours),
            ("other", With(ours, (nameof(Airport.City), "Hounslow"))));
        var airport = LoadedAirport();

        await Accept();

        Assert.Equal(ours, VerificationCheckedColumns.Project(airport));
        Assert.True(airport.IsVerified);
        await _airports.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    // ── What is written ─────────────────────────────────────────────────────

    [Fact]
    public async Task Handle_WritesOnlyTheDifferingColumns_MatchingColumnsKeepOurFormatting()
    {
        AirportMismatch(
            (nameof(Airport.Name), "LONDON  HEATHROW"), // the same name by the text rule
            (nameof(Airport.Latitude), "51.4749"), // within 0.01 degrees
            (nameof(Airport.City), "Hounslow")); // different
        var airport = LoadedAirport();

        await Accept();

        Assert.Equal("London Heathrow", airport.Name);
        Assert.Equal(51.4706, airport.Latitude);
        Assert.Equal("Hounslow", airport.City);
    }

    [Fact]
    public async Task Handle_ParsesEveryAirportColumnKind()
    {
        AirportMismatch(
            (nameof(Airport.Iata), " lgw "),
            (nameof(Airport.Icao), "egkk"),
            (nameof(Airport.Name), "London Gatwick"),
            (nameof(Airport.City), " Crawley "),
            (nameof(Airport.CountryCode), "je "),
            (nameof(Airport.Latitude), "51.1481"),
            (nameof(Airport.Longitude), " -0.190278 "),
            (nameof(Airport.ElevationFt), "202"));
        _airports.FindByIataAsync("LGW", Arg.Any<CancellationToken>()).ReturnsNull();
        _countries.FindByCodeAsync("JE", Arg.Any<CancellationToken>()).Returns(Country.Create("JE", "Jersey"));
        var airport = LoadedAirport();

        await Accept();

        Assert.Equal("LGW", airport.Iata);
        Assert.Equal("EGKK", airport.Icao);
        Assert.Equal("London Gatwick", airport.Name);
        Assert.Equal("Crawley", airport.City);
        Assert.Equal("JE", airport.CountryCode);
        Assert.Equal(51.1481, airport.Latitude);
        Assert.Equal(-0.190278, airport.Longitude);
        Assert.Equal(202, airport.ElevationFt);
        await _airports.Received(1).FindByIataAsync("LGW", Arg.Any<CancellationToken>());
        await _countries.Received(1).FindByCodeAsync("JE", Arg.Any<CancellationToken>());
    }

    [Theory]
    [InlineData("202.5", 203)]
    [InlineData("202.4", 202)]
    [InlineData("-10.5", -11)]
    [InlineData("1e3", 1000)]
    public async Task Handle_ADecimalElevation_IsRoundedToTheNearestFoot(string elevation, int expected)
    {
        AirportMismatch((nameof(Airport.ElevationFt), elevation));
        var airport = LoadedAirport();

        await Accept();

        Assert.Equal(expected, airport.ElevationFt);
    }

    [Fact]
    public async Task Handle_AnEmptySourceValueForAnOptionalColumn_ClearsIt()
    {
        AirportMismatch((nameof(Airport.Icao), ""), (nameof(Airport.Latitude), "  "), (nameof(Airport.ElevationFt), null));
        var airport = LoadedAirport();

        await Accept();

        Assert.Null(airport.Icao);
        Assert.Null(airport.Latitude);
        Assert.Null(airport.ElevationFt);
        Assert.True(airport.IsVerified);
    }

    [Theory]
    [InlineData(nameof(Airport.Iata), "")]
    [InlineData(nameof(Airport.Name), "  ")]
    [InlineData(nameof(Airport.City), null)]
    [InlineData(nameof(Airport.CountryCode), "")]
    public async Task Handle_ARequiredAirportColumnWouldBecomeEmpty_Is409_AndWritesNothing(string column, string? value)
    {
        AirportMismatch((column, value), (nameof(Airport.Icao), "EGKK"));
        var airport = LoadedAirport();

        var ex = await AcceptFails(409);

        Assert.Contains($"required Airport {column} empty", ex.Message);
        AssertUnchanged(airport);
        await AssertNothingSaved();
    }

    [Theory]
    [InlineData(nameof(Airport.Latitude), "north")]
    [InlineData(nameof(Airport.Longitude), "NaN")]
    [InlineData(nameof(Airport.Latitude), "Infinity")]
    [InlineData(nameof(Airport.ElevationFt), "high")]
    [InlineData(nameof(Airport.ElevationFt), "1e20")]
    public async Task Handle_AnUnparseableSourceValue_Is409_AndWritesNothing(string column, string value)
    {
        AirportMismatch((column, value), (nameof(Airport.City), "Hounslow"));
        var airport = LoadedAirport();

        var ex = await AcceptFails(409);

        Assert.Contains($"{column} value could not be parsed", ex.Message);
        AssertUnchanged(airport);
        await AssertNothingSaved();
    }

    // ── Unique columns: a value another row already has ────────────────────

    private const string Airlines = "airlines";

    private static Airline BritishAirways() => WithId(Airline.Create("BA", "BAW", "British Airways", "GB", "OW"), EntityId);

    private Airline LoadedAirline()
    {
        var airline = BritishAirways();
        _airlines.GetByIdAsync(EntityId, Arg.Any<CancellationToken>()).Returns(airline);
        return airline;
    }

    /// <summary>British Airways checked against an airline source, which returned it with <paramref name="sourceOverrides"/>.</summary>
    private VerificationRequest AirlineMismatch(params (string Column, string? Value)[] sourceOverrides)
    {
        var ours = VerificationCheckedColumns.Project(BritishAirways());
        return CheckedAsMismatch(VerificationEntityType.Airline, "42", ours, (Airlines, With(ours, sourceOverrides)));
    }

    private static void AssertUnchanged(Airline airline)
    {
        Assert.Equal(VerificationCheckedColumns.Project(BritishAirways()), VerificationCheckedColumns.Project(airline));
        Assert.False(airline.IsVerified);
    }

    [Fact]
    public async Task Handle_TheNewIataBelongsToAnotherAirport_Is409_AndWritesNothing()
    {
        AirportMismatch((nameof(Airport.Iata), "LGW"), (nameof(Airport.City), "Crawley"));
        var airport = LoadedAirport();
        _airports.FindByIataAsync("LGW", Arg.Any<CancellationToken>())
            .Returns(WithId(Airport.Create("LGW", "EGKK", "London Gatwick", "Crawley", "GB"), 99));

        var ex = await AcceptFails(409);

        Assert.Contains("Airport Iata LGW already belongs to Airport 99", ex.Message);
        AssertUnchanged(airport);
        await AssertNothingSaved();
    }

    [Fact]
    public async Task Handle_TheNewIcaoBelongsToAnotherAirport_Is409_AndWritesNothing()
    {
        // Lower-case from the source: the lookup uses the upper-cased form the airport would store.
        AirportMismatch((nameof(Airport.Icao), "egkk"), (nameof(Airport.City), "Crawley"));
        var airport = LoadedAirport();
        _airports.FindByIcaoAsync("EGKK", Arg.Any<CancellationToken>())
            .Returns(WithId(Airport.Create("LGW", "EGKK", "London Gatwick", "Crawley", "GB"), 99));

        var ex = await AcceptFails(409);

        Assert.Contains("Airport Icao EGKK already belongs to Airport 99", ex.Message);
        AssertUnchanged(airport);
        await AssertNothingSaved();
    }

    [Fact]
    public async Task Handle_AUniqueLookupThatFindsThisSameAirport_IsNotACollision()
    {
        AirportMismatch((nameof(Airport.Icao), "EGKK"));
        var airport = LoadedAirport();
        _airports.FindByIcaoAsync("EGKK", Arg.Any<CancellationToken>()).Returns(airport);

        await Accept();

        Assert.Equal("EGKK", airport.Icao);
        await _airports.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_UniqueColumnsThisAcceptDoesNotWriteOrClears_AreNotLookedUp()
    {
        AirportMismatch((nameof(Airport.Icao), ""), (nameof(Airport.City), "Hounslow"));
        var airport = LoadedAirport();

        await Accept();

        Assert.Null(airport.Icao);
        await _airports.DidNotReceive().FindByIataAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _airports.DidNotReceive().FindByIcaoAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _countries.DidNotReceive().FindByCodeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // ── Country codes: a code our countries table doesn't have ─────────────

    /// <summary><c>airports.CountryCode</c> references <c>countries</c> by foreign key, and the edit rules
    /// only check the code's format: without the lookup this save fails on the constraint, a 500.</summary>
    [Fact]
    public async Task Handle_ANewAirportCountryCodeThatIsNotInCountries_Is409_NamingIt_AndWritesNothing()
    {
        AirportMismatch((nameof(Airport.CountryCode), "xk"), (nameof(Airport.City), "Pristina"));
        var airport = LoadedAirport();
        _countries.FindByCodeAsync("XK", Arg.Any<CancellationToken>()).ReturnsNull();

        var ex = await AcceptFails(409);

        Assert.Equal("Airport CountryCode XK is not a country we have; add the country first.", ex.Message);
        AssertUnchanged(airport);
        await AssertNothingSaved();
    }

    [Fact]
    public async Task Handle_ANewAirlineCountryCodeThatIsNotInCountries_Is409_NamingIt_AndWritesNothing()
    {
        AirlineMismatch((nameof(Airline.CountryCode), "XK"), (nameof(Airline.Name), "British Airways Plc"));
        var airline = LoadedAirline();
        _countries.FindByCodeAsync("XK", Arg.Any<CancellationToken>()).ReturnsNull();

        var ex = await AcceptFails(409, source: Airlines);

        Assert.Equal("Airline CountryCode XK is not a country we have; add the country first.", ex.Message);
        AssertUnchanged(airline);
        await AssertNothingSaved();
    }

    /// <summary>A malformed code is named by the edit rules, not looked up.</summary>
    [Fact]
    public async Task Handle_AMalformedCountryCode_IsRefusedByTheEditRules_BeforeAnyCountryLookup()
    {
        AirportMismatch((nameof(Airport.CountryCode), "GBR"));
        var airport = LoadedAirport();

        var ex = await AcceptFails(409);

        Assert.Contains("with an invalid CountryCode", ex.Message);
        AssertUnchanged(airport);
        await _countries.DidNotReceive().FindByCodeAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // ── An open request for the same entity ─────────────────────────────────

    /// <summary>Reference rows carry no concurrency token: a newer check still open for this airport
    /// could save its own <c>MarkVerificationFailed</c> over the accepted row, so accept waits for it.</summary>
    [Fact]
    public async Task Handle_TheEntityHasAnOpenVerificationRequest_Is409_AndWritesNothing()
    {
        var request = AirportMismatch((nameof(Airport.City), "Hounslow"));
        var airport = LoadedAirport();
        _requests.HasOpenRequestAsync(VerificationEntityType.Airport, "42", Arg.Any<CancellationToken>()).Returns(true);

        var ex = await AcceptFails(409);

        Assert.Equal("Airport 42 has an open verification request; accept once it has finished.", ex.Message);
        AssertUnchanged(airport);
        Assert.Equal(VerificationRequestStatus.Mismatch, request.Status);
        await AssertNothingSaved();
    }

    [Fact]
    public async Task Handle_TheNewIataBelongsToAnotherAirline_Is409_AndWritesNothing()
    {
        AirlineMismatch((nameof(Airline.Iata), "vs"), (nameof(Airline.Name), "British Airways Plc"));
        var airline = LoadedAirline();
        _airlines.FindByIataAsync("VS", Arg.Any<CancellationToken>())
            .Returns(WithId(Airline.Create("VS", "VIR", "Virgin Atlantic", "GB"), 99));

        var ex = await AcceptFails(409, source: Airlines);

        Assert.Contains("Airline Iata VS already belongs to Airline 99", ex.Message);
        AssertUnchanged(airline);
        await AssertNothingSaved();
    }

    [Fact]
    public async Task Handle_TheNewIcaoBelongsToAnotherAirline_Is409_AndWritesNothing()
    {
        AirlineMismatch((nameof(Airline.Icao), "VIR"), (nameof(Airline.Name), "British Airways Plc"));
        var airline = LoadedAirline();
        _airlines.FindByIcaoAsync("VIR", Arg.Any<CancellationToken>())
            .Returns(WithId(Airline.Create("VS", "VIR", "Virgin Atlantic", "GB"), 99));

        var ex = await AcceptFails(409, source: Airlines);

        Assert.Contains("Airline Icao VIR already belongs to Airline 99", ex.Message);
        AssertUnchanged(airline);
        await AssertNothingSaved();
    }

    [Fact]
    public async Task Handle_TheNewAlpha3BelongsToAnotherCountry_Is409_AndWritesNothing()
    {
        CountryMismatch((nameof(Country.Alpha3), "fra"), (nameof(Country.Name), "Britain"));
        var country = LoadedCountry();
        _countries.FindByAlpha3Async("FRA", Arg.Any<CancellationToken>())
            .Returns(Country.Create("FR", "France", "FRA", 250));

        var ex = await AcceptFails(409, source: Mledoze);

        Assert.Contains("Country Alpha3 FRA already belongs to Country FR", ex.Message);
        AssertUnchanged(country);
        await AssertNothingSaved();
    }

    [Fact]
    public async Task Handle_TheNewNumericCodeBelongsToAnotherCountry_Is409_AndWritesNothing()
    {
        CountryMismatch((nameof(Country.NumericCode), "250"), (nameof(Country.Name), "Britain"));
        var country = LoadedCountry();
        _countries.FindByNumericCodeAsync(250, Arg.Any<CancellationToken>())
            .Returns(Country.Create("FR", "France", "FRA", 250));

        var ex = await AcceptFails(409, source: Mledoze);

        Assert.Contains("Country NumericCode 250 already belongs to Country FR", ex.Message);
        AssertUnchanged(country);
        await AssertNothingSaved();
    }

    // ── The admin edit's rules ──────────────────────────────────────────────

    public static TheoryData<string, string> ValuesTheAirportEditRulesRefuse => new()
    {
        { nameof(Airport.Iata), "L1" }, // IATA is three letters
        { nameof(Airport.Icao), "EGLLX" }, // five characters: ICAO is four letters, in a varchar(4)
        { nameof(Airport.Name), new string('N', 201) }, // over the 200-character limit
        { nameof(Airport.City), new string('C', 201) },
        { nameof(Airport.CountryCode), "GBR" } // ISO 3166-1 alpha-2
    };

    [Theory]
    [MemberData(nameof(ValuesTheAirportEditRulesRefuse))]
    public async Task Handle_ASourceValueTheAirportEditRulesRefuse_Is409_NamingTheColumn_AndWritesNothing(string column, string value)
    {
        AirportMismatch((column, value), (nameof(Airport.Latitude), "51.5"));
        var airport = LoadedAirport();

        var ex = await AcceptFails(409);

        Assert.Contains($"with an invalid {column}", ex.Message);
        AssertUnchanged(airport);
        await AssertNothingSaved();
    }

    [Theory]
    [InlineData(nameof(Airline.Iata), "B-A")] // 2-3 letters or digits
    [InlineData(nameof(Airline.Icao), "BAWXX")] // 3-4 letters
    public async Task Handle_ASourceValueTheAirlineEditRulesRefuse_Is409_NamingTheColumn_AndWritesNothing(string column, string value)
    {
        AirlineMismatch((column, value), (nameof(Airline.Name), "British Airways Plc"));
        var airline = LoadedAirline();

        var ex = await AcceptFails(409, source: Airlines);

        Assert.Contains($"with an invalid {column}", ex.Message);
        AssertUnchanged(airline);
        await AssertNothingSaved();
    }

    // ── Verify, and the request ─────────────────────────────────────────────

    [Fact]
    public async Task Handle_VerifiesTheEntityAsTheActor_WithTheAcceptedSource_AndSavesOnce()
    {
        AirportMismatch((nameof(Airport.City), "Hounslow"));
        var airport = LoadedAirport();
        airport.MarkVerificationFailed("City differs (ourairports)");

        await Accept(verifiedBy: "admin-oid-9");

        Assert.True(airport.IsVerified);
        Assert.Equal("admin-oid-9", airport.VerifiedBy);
        Assert.Equal(OurAirports, airport.VerificationSource);
        Assert.Equal(Now, airport.VerifiedAt);
        Assert.Null(airport.VerificationIssue);
        await _airports.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_TheRequestStaysMismatch_WithWhatTheCheckStored()
    {
        var request = AirportMismatch((nameof(Airport.City), "Hounslow"));
        var differencesJson = request.DifferencesJson;
        var sourceValuesJson = request.SourceValuesJson;
        LoadedAirport();

        await Accept();

        Assert.Equal(VerificationRequestStatus.Mismatch, request.Status);
        Assert.Equal(differencesJson, request.DifferencesJson);
        Assert.Equal(sourceValuesJson, request.SourceValuesJson);
        Assert.Equal(CheckedAt, request.CompletedUtc);
    }

    [Fact]
    public async Task Handle_ASecondAccept_Is409_BecauseTheColumnNowHoldsTheSourcesValue()
    {
        AirportMismatch((nameof(Airport.City), "Hounslow"));
        var airport = LoadedAirport();
        await Accept();

        var ex = await AcceptFails(409);

        Assert.Contains("City has changed since the check", ex.Message);
        Assert.Equal("Hounslow", airport.City);
        await _airports.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    // ── Country ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Handle_Country_WritesTheDifferingColumns_ParsingEachKind_AndVerifies()
    {
        CountryMismatch(
            (nameof(Country.Name), "United Kingdom of Great Britain"),
            (nameof(Country.Alpha3), " gbx"),
            (nameof(Country.NumericCode), "004"),
            (nameof(Country.Region), "  europe "), // the same region by the text rule
            (nameof(Country.Subregion), ""));
        var country = LoadedCountry();

        await Accept(source: Mledoze);

        Assert.Equal("United Kingdom of Great Britain", country.Name);
        Assert.Equal("GBX", country.Alpha3);
        Assert.Equal(4, country.NumericCode);
        Assert.Equal("Europe", country.Region);
        Assert.Null(country.Subregion);
        Assert.False(country.IsEU);
        Assert.False(country.IsSchengen);
        Assert.False(country.IsEEA);
        Assert.True(country.IsVerified);
        Assert.Equal(Actor, country.VerifiedBy);
        Assert.Equal(Mledoze, country.VerificationSource);
        await _countries.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Handle_Country_ANameThatOnlyGainedAnAccent_HasNotChanged()
    {
        var atCheck = Country.Create("TR", "Turkiye", "TUR", 792, "Asia", "Western Asia");
        var ours = VerificationCheckedColumns.Project(atCheck);
        CheckedAsMismatch(VerificationEntityType.Country, "TR", ours, (Mledoze, With(ours, (nameof(Country.Name), "Turkey"))));
        var country = LoadedCountry(Country.Create("TR", "Türkiye", "TUR", 792, "Asia", "Western Asia"));

        await Accept(source: Mledoze);

        Assert.Equal("Turkey", country.Name);
    }

    [Fact]
    public async Task Handle_Country_ANameChangedSinceTheCheck_Is409()
    {
        CountryMismatch((nameof(Country.Name), "United Kingdom of Great Britain"));
        var country = LoadedCountry();
        country.Update("Britain", country.Alpha3, country.NumericCode, country.Region, country.Subregion,
            country.IsEU, country.IsSchengen, country.IsEEA);

        await AcceptFails(409, source: Mledoze);

        Assert.Equal("Britain", country.Name);
        await AssertNothingSaved();
    }

    [Theory]
    [InlineData(nameof(Country.Name), "")]
    [InlineData(nameof(Country.NumericCode), "82a")]
    [InlineData(nameof(Country.Code), "UK")] // the key: Update has no parameter for it
    public async Task Handle_Country_AValueThatCannotBeApplied_Is409_AndWritesNothing(string column, string value)
    {
        CountryMismatch((column, value), (nameof(Country.Region), "Europe and Asia"));
        var country = LoadedCountry();

        await AcceptFails(409, source: Mledoze);

        AssertUnchanged(country);
        await AssertNothingSaved();
    }

    [Fact]
    public async Task Handle_Country_ASecondAccept_Is409()
    {
        CountryMismatch((nameof(Country.Name), "United Kingdom of Great Britain"));
        LoadedCountry();
        await Accept(source: Mledoze);

        await AcceptFails(409, source: Mledoze);

        await _countries.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    // ── Airline ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Handle_Airline_WritesTheDifferingColumns_KeepingTheAllianceCode()
    {
        var ours = VerificationCheckedColumns.Project(Airline.Create("BA", "BAW", "British Airways", "GB", "OW"));
        CheckedAsMismatch(VerificationEntityType.Airline, "42", ours,
            ("airlines", With(ours, (nameof(Airline.Name), "British Airways Plc"), (nameof(Airline.Iata), ""))));
        var airline = WithId(Airline.Create("BA", "BAW", "British Airways", "GB", "OW"), EntityId);
        _airlines.GetByIdAsync(EntityId, Arg.Any<CancellationToken>()).Returns(airline);

        await Accept(source: "airlines");

        Assert.Equal("British Airways Plc", airline.Name);
        Assert.Null(airline.Iata);
        Assert.Equal("OW", airline.AllianceCode);
        Assert.True(airline.IsVerified);
        await _airlines.Received(1).SaveChangesAsync(Arg.Any<CancellationToken>());
    }

    // ── Validator ───────────────────────────────────────────────────────────

    [Theory]
    [InlineData(0L, OurAirports, Actor, "RequestId")]
    [InlineData(RequestId, "", Actor, "Source")]
    [InlineData(RequestId, OurAirports, " ", "VerifiedBy")]
    public void Validator_RejectsAMissingIdSourceOrActor(long requestId, string source, string verifiedBy, string property)
    {
        var result = new AcceptVerificationSourceCommandValidator()
            .Validate(new AcceptVerificationSourceCommand(requestId, source, verifiedBy));

        Assert.Equal(property, Assert.Single(result.Errors).PropertyName);
    }
}
