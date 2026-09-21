using FlightAssistant.Application.Commands.Flights;
using FlightAssistant.Application.Commands.Loyalty;
using FlightAssistant.Application.Commands.Travelers;
using FlightAssistant.Application.Interfaces.ExternalServices;
using FlightAssistant.Application.Queries.Flights;
using FlightAssistant.Application.Queries.Lounges;
using FlightAssistant.Application.Validators;
using FlightAssistant.Application.Commands.Admin.Airlines;
using FlightAssistant.Application.Commands.Admin.Airports;
using FlightAssistant.Application.Commands.Admin.Lounges;
using FlightAssistant.Application.Commands.Admin.Loyalty;
using FlightAssistant.Application.Commands.Admin.Import;
using FlightAssistant.Application.Commands.Admin.Travelers;
using FlightAssistant.Contracts.Paging;
using FluentValidation;
using Microsoft.Extensions.Time.Testing;

namespace FlightAssistant.Application.Tests.Validators;

public class ValidatorEdgeCaseTests
{
    // ── Helpers ──────────────────────────────────────────────────────────────

    private static void AssertValid<T>(AbstractValidator<T> v, T cmd)
    {
        var result = v.Validate(cmd);
        Assert.True(result.IsValid, string.Join("; ", result.Errors.Select(e => e.ErrorMessage)));
    }

    private static void AssertInvalid<T>(AbstractValidator<T> v, T cmd, string? containsField = null)
    {
        var result = v.Validate(cmd);
        Assert.False(result.IsValid);
        if (containsField is not null)
            Assert.Contains(result.Errors, e => e.PropertyName.Contains(containsField, StringComparison.OrdinalIgnoreCase));
    }

    // ── FlightNumber regex ────────────────────────────────────────────────────

    [Theory]
    [InlineData("BA123")]      // standard
    [InlineData("BA1234")]     // 4-digit
    [InlineData("BA1")]        // 1-digit
    [InlineData("BA123A")]     // suffix letter
    [InlineData("ba123")]      // lowercase — normalized, not rejected
    [InlineData("bA123a")]     // mixed case
    public void TrackFlight_ValidFlightNumbers_Pass(string flightNumber)
    {
        var v = new TrackFlightCommandValidator(TimeProvider.System);
        var cmd = ValidTrackFlightCommand() with { FlightNumber = flightNumber };
        AssertValid(v, cmd);
    }

    [Theory]
    [InlineData("BA12345")]    // 5 digits — exceeds the 1-4 digit run
    [InlineData("BA")]         // prefix only, no digits at all
    [InlineData("BA1234AA")]   // two trailing letters — only one is allowed
    [InlineData("")]           // empty
    public void TrackFlight_InvalidFlightNumbers_Fail(string flightNumber)
    {
        var v = new TrackFlightCommandValidator(TimeProvider.System);
        var cmd = ValidTrackFlightCommand() with { FlightNumber = flightNumber };
        AssertInvalid(v, cmd, "FlightNumber");
    }

    // ── Past flights (#350) ──────────────────────────────────────────────────
    // ServiceDate is the origin's local date, so comparing it with today's UTC date rejected
    // tonight's flight west of UTC once the UTC date had rolled over, even though search still
    // returned it. A flight is in the past only once its scheduled arrival is.

    private static TrackFlightCommandValidator TrackValidatorAt(int day, int hour) =>
        new(new FakeTimeProvider(new DateTimeOffset(2026, 9, day, hour, 0, 0, TimeSpan.Zero)));

    [Fact]
    public void TrackFlight_TonightsFlightAfterUtcMidnight_Passes()
    {
        // LAX 22:30 PDT on 2026-09-14 is 05:30 UTC on the 15th. It's 20:00 PDT, 03:00 UTC on the 15th.
        var v = TrackValidatorAt(day: 15, hour: 3);
        var cmd = ValidTrackFlightCommand() with
        {
            ServiceDate = new DateOnly(2026, 9, 14),
            ScheduledDepartureUtc = new DateTime(2026, 9, 15, 5, 30, 0, DateTimeKind.Utc),
            ScheduledArrivalUtc = new DateTime(2026, 9, 15, 14, 0, 0, DateTimeKind.Utc)
        };
        AssertValid(v, cmd);
    }

    [Fact]
    public void TrackFlight_StillInTheAirAfterItsServiceDate_Passes()
    {
        // LHR→SIN departed 20:00 UTC on the 14th and lands 09:00 UTC on the 15th. It's 04:00 UTC.
        var v = TrackValidatorAt(day: 15, hour: 4);
        var cmd = ValidTrackFlightCommand() with
        {
            ServiceDate = new DateOnly(2026, 9, 14),
            ScheduledDepartureUtc = new DateTime(2026, 9, 14, 20, 0, 0, DateTimeKind.Utc),
            ScheduledArrivalUtc = new DateTime(2026, 9, 15, 9, 0, 0, DateTimeKind.Utc)
        };
        AssertValid(v, cmd);
    }

    [Fact]
    public void TrackFlight_ScheduledArrivalInThePast_Fails()
    {
        // Landed 16:00 UTC yesterday. It's 12:00 UTC.
        var v = TrackValidatorAt(day: 15, hour: 12);
        var cmd = ValidTrackFlightCommand() with
        {
            ServiceDate = new DateOnly(2026, 9, 14),
            ScheduledDepartureUtc = new DateTime(2026, 9, 14, 8, 0, 0, DateTimeKind.Utc),
            ScheduledArrivalUtc = new DateTime(2026, 9, 14, 16, 0, 0, DateTimeKind.Utc)
        };
        AssertInvalid(v, cmd, "ScheduledArrivalUtc");
    }

    [Fact]
    public void TrackFlight_ArrivalBeforeDeparture_Fails()
    {
        var v = new TrackFlightCommandValidator(TimeProvider.System);
        var now = DateTime.UtcNow;
        var cmd = ValidTrackFlightCommand() with
        {
            ScheduledDepartureUtc = now.AddHours(8),
            ScheduledArrivalUtc = now.AddHours(2)  // before departure
        };
        AssertInvalid(v, cmd, "ScheduledArrivalUtc");
    }

    // ── IATA codes ────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("LHR")]
    [InlineData("JFK")]
    [InlineData("SYD")]
    [InlineData("lhr")]    // lowercase — normalized, not rejected
    [InlineData("jFk")]    // mixed case
    public void TrackFlight_ValidAirportIata_Pass(string iata)
    {
        var v = new TrackFlightCommandValidator(TimeProvider.System);
        var cmd = ValidTrackFlightCommand() with { DepartureAirportIata = iata };
        AssertValid(v, cmd);
    }

    [Theory]
    [InlineData("LHRX")]   // 4 chars
    [InlineData("LH")]     // 2 chars
    [InlineData("1LH")]    // digit prefix
    public void TrackFlight_InvalidAirportIata_Fail(string iata)
    {
        var v = new TrackFlightCommandValidator(TimeProvider.System);
        var cmd = ValidTrackFlightCommand() with { DepartureAirportIata = iata };
        AssertInvalid(v, cmd, "DepartureAirportIata");
    }

    // ── Email ─────────────────────────────────────────────────────────────────

    [Fact]
    public void RegisterTraveler_ValidEmail_Passes()
    {
        var v = new RegisterTravelerCommandValidator();
        AssertValid(v, new RegisterTravelerCommand(Guid.NewGuid(), "user@example.com", null));
    }

    [Fact]
    public void RegisterTraveler_EmailExceeds254Chars_Fails()
    {
        var v = new RegisterTravelerCommandValidator();
        var longEmail = new string('a', 249) + "@b.com"; // 255 chars — exceeds 254
        AssertInvalid(v, new RegisterTravelerCommand(Guid.NewGuid(), longEmail, null), "Email");
    }

    [Theory]
    [InlineData("notanemail")]
    [InlineData("@nodomain")]
    [InlineData("")]
    public void RegisterTraveler_InvalidEmail_Fails(string email)
    {
        var v = new RegisterTravelerCommandValidator();
        AssertInvalid(v, new RegisterTravelerCommand(Guid.NewGuid(), email, null), "Email");
    }

    // ── Paging ────────────────────────────────────────────────────────────────

    [Fact]
    public void PagedRequest_Page0_Fails()
    {
        var v = new PagedRequestValidator();
        AssertInvalid(v, new PagedRequest { Page = 0, PageSize = 10 }, "Page");
    }

    [Fact]
    public void PagedRequest_Page1_Passes()
    {
        var v = new PagedRequestValidator();
        AssertValid(v, new PagedRequest { Page = 1, PageSize = 10 });
    }

    [Fact]
    public void PagedRequest_PageSize0_Fails()
    {
        var v = new PagedRequestValidator();
        AssertInvalid(v, new PagedRequest { Page = 1, PageSize = 0 }, "PageSize");
    }

    [Fact]
    public void PagedRequest_PageSize201_Fails()
    {
        var v = new PagedRequestValidator();
        AssertInvalid(v, new PagedRequest { Page = 1, PageSize = 201 }, "PageSize");
    }

    [Fact]
    public void PagedRequest_PageSize200_Passes()
    {
        var v = new PagedRequestValidator();
        AssertValid(v, new PagedRequest { Page = 1, PageSize = 200 });
    }

    // ── Paging: Page/PageSize offset overflow (#316) ────────────────────────────
    // Every paged repository computes the SQL OFFSET as (page - 1) * pageSize. Before #316 that was
    // unbounded int arithmetic: a large enough Page overflowed it, wrapped around to a negative
    // number, and Npgsql rejected the negative OFFSET as an unhandled 500. These three cases prove
    // the validator's bound sits exactly at the overflow boundary and genuinely reads both fields,
    // not just Page.

    /// <summary>
    /// An overflowing (Page, PageSize) pair is rejected, and grouped specifically under Page — not
    /// PageSize, which is independently legal (200 is the maximum allowed value, not an invalid
    /// one). Asserts the property name exactly rather than through AssertInvalid's Contains-based
    /// match: "PageSize" itself contains the substring "Page", so a Contains check here would also
    /// pass if the rule were wrongly grouped under PageSize instead of Page.
    /// </summary>
    [Fact]
    public void PagedRequest_PageOverflowsOffset_Fails_GroupedExactlyUnderPage()
    {
        var v = new PagedRequestValidator();
        var result = v.Validate(new PagedRequest { Page = int.MaxValue, PageSize = 200 });

        Assert.False(result.IsValid);
        var error = Assert.Single(result.Errors);
        Assert.Equal("Page", error.PropertyName);
    }

    /// <summary>
    /// The largest Page that does not overflow the offset at PageSize = 200 — 10,737,419, since
    /// (10,737,419 - 1) * 200 = 2,147,483,600 &lt;= int.MaxValue — is accepted, and the very next
    /// Page (10,737,420: (10,737,420 - 1) * 200 = 2,147,483,800 &gt; int.MaxValue) is rejected. Pins
    /// the bound to the overflow boundary itself, off-by-one in both directions.
    /// </summary>
    [Fact]
    public void PagedRequest_LargestLegalPageAtPageSize200_Passes_AndTheNextPageFails()
    {
        var v = new PagedRequestValidator();

        AssertValid(v, new PagedRequest { Page = 10_737_419, PageSize = 200 });
        AssertInvalid(v, new PagedRequest { Page = 10_737_420, PageSize = 200 }, "Page");
    }

    /// <summary>
    /// Proves the rule genuinely reads PageSize off the request rather than hardcoding a fixed
    /// ceiling on Page: Page = 10,737,420 comfortably fits at PageSize = 1
    /// ((10,737,420 - 1) * 1 = 10,737,419) but is exactly the first Page the rule rejects at
    /// PageSize = 200 (see the boundary test above). A Page-only bound could not distinguish these
    /// two requests.
    /// </summary>
    [Fact]
    public void PagedRequest_SamePage_PassesAtPageSize1_ButFailsAtPageSize200()
    {
        var v = new PagedRequestValidator();

        AssertValid(v, new PagedRequest { Page = 10_737_420, PageSize = 1 });
        AssertInvalid(v, new PagedRequest { Page = 10_737_420, PageSize = 200 }, "Page");
    }

    // ── Loyalty membership ────────────────────────────────────────────────────

    [Fact]
    public void AddMembership_AccountNumberExceeds64_Fails()
    {
        var v = new AddLoyaltyMembershipCommandValidator();
        var cmd = new AddLoyaltyMembershipCommand(Guid.NewGuid(), 1, new string('A', 65), null, null);
        AssertInvalid(v, cmd, "AccountNumber");
    }

    [Fact]
    public void AddMembership_AccountNumber64Chars_Passes()
    {
        var v = new AddLoyaltyMembershipCommandValidator();
        var cmd = new AddLoyaltyMembershipCommand(Guid.NewGuid(), 1, new string('A', 64), null, null);
        AssertValid(v, cmd);
    }

    [Fact]
    public void AddMembership_PastTierExpiry_Fails()
    {
        var v = new AddLoyaltyMembershipCommandValidator();
        var cmd = new AddLoyaltyMembershipCommand(Guid.NewGuid(), 1, "ACC1", null,
            DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-1)));
        AssertInvalid(v, cmd, "TierExpiryDate");
    }

    [Fact]
    public void AddMembership_FutureTierExpiry_Passes()
    {
        var v = new AddLoyaltyMembershipCommandValidator();
        var cmd = new AddLoyaltyMembershipCommand(Guid.NewGuid(), 1, "ACC1", null,
            DateOnly.FromDateTime(DateTime.UtcNow.AddDays(30)));
        AssertValid(v, cmd);
    }

    [Fact]
    public void AddMembership_NullTierExpiry_Passes()
    {
        var v = new AddLoyaltyMembershipCommandValidator();
        var cmd = new AddLoyaltyMembershipCommand(Guid.NewGuid(), 1, "ACC1", null, null);
        AssertValid(v, cmd);
    }

    [Fact]
    public void UpdateMembership_PastTierExpiry_Fails()
    {
        var v = new UpdateLoyaltyMembershipCommandValidator();
        var cmd = new UpdateLoyaltyMembershipCommand(Guid.NewGuid(), Guid.NewGuid(), "ACC1", null,
            DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-1)));
        AssertInvalid(v, cmd, "TierExpiryDate");
    }

    [Fact]
    public void UpdateMembership_NullAccountNumber_Passes()
    {
        var v = new UpdateLoyaltyMembershipCommandValidator();
        AssertValid(v, new UpdateLoyaltyMembershipCommand(Guid.NewGuid(), Guid.NewGuid(), null, 5, null));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void UpdateMembership_BlankAccountNumber_Fails(string accountNumber)
    {
        var v = new UpdateLoyaltyMembershipCommandValidator();
        AssertInvalid(v, new UpdateLoyaltyMembershipCommand(Guid.NewGuid(), Guid.NewGuid(), accountNumber, null, null), "AccountNumber");
    }

    // ── AddDeviceToken platform whitelist ─────────────────────────────────────

    [Fact]
    public void AddDeviceToken_IosPlatform_Passes()
    {
        var v = new AddDeviceTokenCommandValidator();
        AssertValid(v, new AddDeviceTokenCommand(Guid.NewGuid(), "ios", "token123"));
    }

    [Theory]
    [InlineData("android")]
    [InlineData("web")]
    [InlineData("windows")]
    public void AddDeviceToken_UnsupportedPlatform_Fails(string platform)
    {
        var v = new AddDeviceTokenCommandValidator();
        AssertInvalid(v, new AddDeviceTokenCommand(Guid.NewGuid(), platform, "token123"), "Platform");
    }

    [Fact]
    public void AddDeviceToken_TokenExceeds2048_Fails()
    {
        var v = new AddDeviceTokenCommandValidator();
        AssertInvalid(v, new AddDeviceTokenCommand(Guid.NewGuid(), "ios", new string('x', 2049)), "Token");
    }

    // ── Admin: Airline ────────────────────────────────────────────────────────

    [Fact]
    public void CreateAirline_NameExceeds200_Fails()
    {
        var v = new CreateAirlineCommandValidator();
        AssertInvalid(v, new CreateAirlineCommand("BA", null, new string('A', 201), "GB", null), "Name");
    }

    [Fact]
    public void CreateAirline_LowercaseIata_Passes()
    {
        var v = new CreateAirlineCommandValidator();
        AssertValid(v, new CreateAirlineCommand("ba", null, "British Airways", "gb", null));
    }

    [Fact]
    public void CreateAirline_InvalidCountryCode_Fails()
    {
        var v = new CreateAirlineCommandValidator();
        AssertInvalid(v, new CreateAirlineCommand("BA", null, "British Airways", "GBR", null), "CountryCode");
    }

    // ── Admin: Airport ────────────────────────────────────────────────────────

    [Fact]
    public void CreateAirport_LowercaseIata_Passes()
    {
        var v = new CreateAirportCommandValidator();
        AssertValid(v, new CreateAirportCommand("lhr", "egll", "Heathrow", "London", "gb", null, null, null));
    }

    [Fact]
    public void CreateAirport_CityExceeds200_Fails()
    {
        var v = new CreateAirportCommandValidator();
        AssertInvalid(v, new CreateAirportCommand("LHR", null, "Heathrow", new string('A', 201), "GB", null, null, null), "City");
    }

    // ── Admin: Lounge ─────────────────────────────────────────────────────────

    [Fact]
    public void CreateLounge_AmenitiesExceeds1000_Fails()
    {
        var v = new CreateLoungeCommandValidator();
        AssertInvalid(v, new CreateLoungeCommand(1, "Test Lounge", null, null, null, true, false, false, new string('x', 1001)), "Amenities");
    }

    [Fact]
    public void CreateLounge_NameExceeds200_Fails()
    {
        var v = new CreateLoungeCommandValidator();
        AssertInvalid(v, new CreateLoungeCommand(1, new string('A', 201), null, null, null, true, false, false, null), "Name");
    }

    // ── Admin: LoyaltyProgram ─────────────────────────────────────────────────

    [Fact]
    public void CreateLoyaltyProgram_CodeExceeds16_Fails()
    {
        var v = new CreateLoyaltyProgramCommandValidator();
        AssertInvalid(v, new CreateLoyaltyProgramCommand(new string('A', 17), "BA Executive Club", 1, null), "Code");
    }

    // ── Flight search ─────────────────────────────────────────────────────────

    [Fact]
    public void SearchFlights_ValidQuery_Passes()
    {
        var v = new SearchFlightsQueryValidator(DefaultHorizon);
        AssertValid(v, new SearchFlightsQuery("LHR", "JFK", DateOnly.FromDateTime(DateTime.UtcNow)));
    }

    [Theory]
    [InlineData("lhr", "JFK")] // lowercase From — normalized, not rejected
    [InlineData("LHR", "jfk")] // lowercase To
    [InlineData("lhr", "jfk")] // both lowercase
    [InlineData("lHr", "jFk")] // mixed case
    public void SearchFlights_AnyCaseIata_Passes(string from, string to)
    {
        var v = new SearchFlightsQueryValidator(DefaultHorizon);
        AssertValid(v, new SearchFlightsQuery(from, to, DateOnly.FromDateTime(DateTime.UtcNow)));
    }

    [Theory]
    [InlineData("LH", "JFK")]   // 2-char From
    [InlineData("LHR", "JFKK")] // 4-char To
    public void SearchFlights_InvalidIata_Fails(string from, string to)
    {
        var v = new SearchFlightsQueryValidator(DefaultHorizon);
        AssertInvalid(v, new SearchFlightsQuery(from, to, DateOnly.FromDateTime(DateTime.UtcNow)));
    }

    [Theory]
    [InlineData("BA123")]
    [InlineData("U23456")] // numeric-containing airline prefix
    [InlineData("ba123")]  // lowercase — normalized, not rejected
    public void SearchFlights_FlightNumberOnly_Passes(string flightNumber)
    {
        var v = new SearchFlightsQueryValidator(DefaultHorizon);
        AssertValid(v, new SearchFlightsQuery(null, null, DateOnly.FromDateTime(DateTime.UtcNow), flightNumber));
    }

    [Fact]
    public void SearchFlights_FutureDateWithinProviderHorizon_Passes()
    {
        var v = new SearchFlightsQueryValidator(DefaultHorizon);
        AssertValid(v, new SearchFlightsQuery("LHR", "JFK", DateOnly.FromDateTime(DateTime.UtcNow.AddMonths(6))));
    }

    [Fact]
    public void SearchFlights_DateBeyondProviderHorizon_Fails()
    {
        // The schedules endpoint refuses dates past a rolling ~1-year horizon; rejecting here
        // turns what would be an upstream 502 into a clear 400.
        var v = new SearchFlightsQueryValidator(DefaultHorizon);
        AssertInvalid(v, new SearchFlightsQuery("LHR", "JFK", DateOnly.FromDateTime(DateTime.UtcNow.AddMonths(13))));
    }

    [Fact]
    public void SearchFlights_NoRouteAndNoFlightNumber_Fails()
    {
        var v = new SearchFlightsQueryValidator(DefaultHorizon);
        AssertInvalid(v, new SearchFlightsQuery(null, null, DateOnly.FromDateTime(DateTime.UtcNow)));
    }

    [Theory]
    [InlineData("LHR", null)] // only From
    [InlineData(null, "JFK")] // only To
    public void SearchFlights_HalfRoute_Fails(string? from, string? to)
    {
        var v = new SearchFlightsQueryValidator(DefaultHorizon);
        AssertInvalid(v, new SearchFlightsQuery(from, to, DateOnly.FromDateTime(DateTime.UtcNow)));
    }

    [Theory]
    [InlineData("BA")]       // missing number
    [InlineData("B")]        // too short
    [InlineData("BA12345")]  // number too long
    [InlineData("12345")]    // all-digit airline prefix — no such carrier; search used to accept it
    public void SearchFlights_InvalidFlightNumber_Fails(string flightNumber)
    {
        var v = new SearchFlightsQueryValidator(DefaultHorizon);
        AssertInvalid(v, new SearchFlightsQuery(null, null, DateOnly.FromDateTime(DateTime.UtcNow), flightNumber));
    }

    [Theory]
    // Accepted by both:
    [InlineData("BA123")]
    [InlineData("6E4120")]  // digit-first airline prefix
    [InlineData("U21234")]  // letter-then-digit prefix
    [InlineData("BA123A")]  // operational trailing letter — search used to REJECT this
    [InlineData("ba123")]   // case is normalised, not rejected
    // Rejected by both:
    [InlineData("12345")]   // all-digit prefix — search used to ACCEPT this
    [InlineData("BA")]
    [InlineData("B")]
    [InlineData("BA12345")]
    [InlineData("BA123AB")]
    public void FlightNumberRule_IsIdentical_ForSearchAndTrack(string flightNumber)
    {
        // The two validators used to carry two different hand-written regexes, disagreeing in both
        // directions: search accepted an all-digit prefix that track refused, and track accepted a
        // trailing letter that search refused. Either disagreement means the picker can hand a
        // client a flight number POST /flights then rejects — the exact class of defect the search
        // reshape exists to remove. They now share FlightNumberFormat.Pattern; this asserts they
        // agree on every shape rather than trusting that they still read from the same constant.
        var searchAccepts = new SearchFlightsQueryValidator(DefaultHorizon)
            .Validate(new SearchFlightsQuery(null, null, DateOnly.FromDateTime(DateTime.UtcNow), flightNumber))
            .IsValid;

        var trackAccepts = new TrackFlightCommandValidator(TimeProvider.System)
            .Validate(ValidTrackFlightCommand() with { FlightNumber = flightNumber })
            .IsValid;

        Assert.Equal(trackAccepts, searchAccepts);
    }

    // ── Import: Alliances ─────────────────────────────────────────────────────────

    [Fact]
    public void ImportAlliances_EmptyCode_Fails()
    {
        var v = new ImportAlliancesCommandValidator();
        AssertInvalid(v, new ImportAlliancesCommand([new AllianceImportDto("", "Star Alliance")]), "Code");
    }

    [Fact]
    public void ImportAlliances_LowercaseCode_Passes()
    {
        var v = new ImportAlliancesCommandValidator();
        AssertValid(v, new ImportAlliancesCommand([new AllianceImportDto("sa", "Star Alliance")]));
    }

    [Fact]
    public void ImportAlliances_ValidItem_Passes()
    {
        var v = new ImportAlliancesCommandValidator();
        AssertValid(v, new ImportAlliancesCommand([new AllianceImportDto("SA", "Star Alliance")]));
    }

    [Fact]
    public void ImportAlliances_EmptyList_Fails()
    {
        var v = new ImportAlliancesCommandValidator();
        AssertInvalid(v, new ImportAlliancesCommand([]), "Items");
    }

    // ── Import: Countries ─────────────────────────────────────────────────────────

    [Fact]
    public void ImportCountries_InvalidCodeLength_Fails()
    {
        var v = new ImportCountriesCommandValidator();
        AssertInvalid(v, new ImportCountriesCommand([new CountryImportDto("GBR", "UK", null, null, null, null, null, null, null)]), "Code");
    }

    [Fact]
    public void ImportCountries_LowercaseCode_Passes()
    {
        var v = new ImportCountriesCommandValidator();
        AssertValid(v, new ImportCountriesCommand([new CountryImportDto("gb", "UK", "gbr", null, null, null, null, null, null)]));
    }

    [Fact]
    public void ImportCountries_ValidItem_Passes()
    {
        var v = new ImportCountriesCommandValidator();
        AssertValid(v, new ImportCountriesCommand([new CountryImportDto("GB", "United Kingdom", "GBR", 826, null, null, null, null, null)]));
    }

    // ── Import: Airports ──────────────────────────────────────────────────────────

    [Fact]
    public void ImportAirports_InvalidIataLength_Fails()
    {
        var v = new ImportAirportsCommandValidator();
        AssertInvalid(v, new ImportAirportsCommand([
            new AirportImportDto("LHRX", null, "Heathrow", "London", "GB", null, null, null)]), "Iata");
    }

    [Fact]
    public void ImportAirports_LowercaseIata_Passes()
    {
        var v = new ImportAirportsCommandValidator();
        AssertValid(v, new ImportAirportsCommand([
            new AirportImportDto("lhr", "egll", "Heathrow", "London", "gb", null, null, null)]));
    }

    [Fact]
    public void ImportAirports_ValidItem_Passes()
    {
        var v = new ImportAirportsCommandValidator();
        AssertValid(v, new ImportAirportsCommand([
            new AirportImportDto("LHR", "EGLL", "Heathrow", "London", "GB", 51.477, -0.461, 83)]));
    }

    // ── Import: Airlines ──────────────────────────────────────────────────────────

    [Fact]
    public void ImportAirlines_NoBothIataAndIcao_Fails()
    {
        var v = new ImportAirlinesCommandValidator();
        AssertInvalid(v, new ImportAirlinesCommand([
            new AirlineImportDto(null, null, "British Airways", "GB", null)]));
    }

    [Fact]
    public void ImportAirlines_InvalidCountryCode_Fails()
    {
        var v = new ImportAirlinesCommandValidator();
        AssertInvalid(v, new ImportAirlinesCommand([
            new AirlineImportDto("BA", null, "British Airways", "GBR", null)]), "CountryCode");
    }

    [Fact]
    public void ImportAirlines_ValidItemWithIataOnly_Passes()
    {
        var v = new ImportAirlinesCommandValidator();
        AssertValid(v, new ImportAirlinesCommand([
            new AirlineImportDto("BA", null, "British Airways", "GB", null)]));
    }

    // ── Import: LoungeProviders ───────────────────────────────────────────────────

    [Fact]
    public void ImportLoungeProviders_EmptyCode_Fails()
    {
        var v = new ImportLoungeProvidersCommandValidator();
        AssertInvalid(v, new ImportLoungeProvidersCommand([
            new LoungeProviderImportDto("", "AMEX", "Card", null)]), "Code");
    }

    [Fact]
    public void ImportLoungeProviders_InvalidKind_Fails()
    {
        var v = new ImportLoungeProvidersCommandValidator();
        AssertInvalid(v, new ImportLoungeProvidersCommand([
            new LoungeProviderImportDto("AMEX", "American Express", "NotAKind", null)]), "Kind");
    }

    // ── Import: Lounges ───────────────────────────────────────────────────────────

    [Fact]
    public void ImportLounges_TerminalExceeds32_Fails()
    {
        var v = new ImportLoungesCommandValidator();
        var dto = new LoungeImportDto("Test Lounge", "LHR", new string('A', 33), null, null, true, false, false, null, []);
        AssertInvalid(v, new ImportLoungesCommand([dto]), "Terminal");
    }

    [Fact]
    public void ImportLounges_Terminal32Chars_Passes()
    {
        var v = new ImportLoungesCommandValidator();
        var dto = new LoungeImportDto("Test Lounge", "LHR", new string('A', 32), null, null, true, false, false, null, []);
        AssertValid(v, new ImportLoungesCommand([dto]));
    }

    [Fact]
    public void ImportLounges_LocationHintExceeds200_Fails()
    {
        var v = new ImportLoungesCommandValidator();
        var dto = new LoungeImportDto("Test Lounge", "LHR", null, new string('A', 201), null, true, false, false, null, []);
        AssertInvalid(v, new ImportLoungesCommand([dto]), "LocationHint");
    }

    [Fact]
    public void ImportLounges_HoursExceeds200_Fails()
    {
        var v = new ImportLoungesCommandValidator();
        var dto = new LoungeImportDto("Test Lounge", "LHR", null, null, new string('A', 201), true, false, false, null, []);
        AssertInvalid(v, new ImportLoungesCommand([dto]), "Hours");
    }

    [Fact]
    public void ImportLounges_NullTerminalLocationHintHours_Passes()
    {
        var v = new ImportLoungesCommandValidator();
        var dto = new LoungeImportDto("Test Lounge", "LHR", null, null, null, true, false, false, null, []);
        AssertValid(v, new ImportLoungesCommand([dto]));
    }

    // ── Import: LoyaltyPrograms ───────────────────────────────────────────────────

    [Fact]
    public void ImportLoyaltyPrograms_CodeExceeds16_Fails()
    {
        var v = new ImportLoyaltyProgramsCommandValidator();
        AssertInvalid(v, new ImportLoyaltyProgramsCommand([
            new LoyaltyProgramImportDto(new string('A', 17), "Club", "AIRLINE", null, [])]), "Code");
    }

    [Fact]
    public void ImportLoyaltyPrograms_TierWithZeroRank_Fails()
    {
        var v = new ImportLoyaltyProgramsCommandValidator();
        var tier = new LoyaltyTierImportDto("GOLD", "Gold", 0, null, null);
        AssertInvalid(v, new ImportLoyaltyProgramsCommand([
            new LoyaltyProgramImportDto("BAEC", "Executive Club", "AIRLINE", null, [tier])]), "Rank");
    }

    [Fact]
    public void ImportLoyaltyPrograms_ValidItemWithTiers_Passes()
    {
        var v = new ImportLoyaltyProgramsCommandValidator();
        var tier = new LoyaltyTierImportDto("GOLD", "Gold", 1, null, null);
        AssertValid(v, new ImportLoyaltyProgramsCommand([
            new LoyaltyProgramImportDto("BAEC", "Executive Club", "AIRLINE", "BA", [tier])]));
    }

    // ── Import: verification columns (#362) — VerifiedBy ≤ 255, VerificationSource ≤ 100 ──────

    [Fact]
    public void ImportAlliances_VerifiedByExceeds255_Fails()
    {
        var v = new ImportAlliancesCommandValidator();
        AssertInvalid(v, new ImportAlliancesCommand([
            new AllianceImportDto("SA", "Star Alliance", VerifiedBy: new string('A', 256))]), "VerifiedBy");
    }

    [Fact]
    public void ImportAlliances_VerificationSourceExceeds100_Fails()
    {
        var v = new ImportAlliancesCommandValidator();
        AssertInvalid(v, new ImportAlliancesCommand([
            new AllianceImportDto("SA", "Star Alliance", VerificationSource: new string('A', 101))]), "VerificationSource");
    }

    [Fact]
    public void ImportCountries_VerifiedByExceeds255_Fails()
    {
        var v = new ImportCountriesCommandValidator();
        AssertInvalid(v, new ImportCountriesCommand([
            new CountryImportDto("GB", "United Kingdom", null, null, null, null, null, null, null,
                VerifiedBy: new string('A', 256))]), "VerifiedBy");
    }

    [Fact]
    public void ImportCountries_VerificationSourceExceeds100_Fails()
    {
        var v = new ImportCountriesCommandValidator();
        AssertInvalid(v, new ImportCountriesCommand([
            new CountryImportDto("GB", "United Kingdom", null, null, null, null, null, null, null,
                VerificationSource: new string('A', 101))]), "VerificationSource");
    }

    [Fact]
    public void ImportAirports_VerifiedByExceeds255_Fails()
    {
        var v = new ImportAirportsCommandValidator();
        AssertInvalid(v, new ImportAirportsCommand([
            new AirportImportDto("LHR", "EGLL", "Heathrow", "London", "GB", null, null, null,
                VerifiedBy: new string('A', 256))]), "VerifiedBy");
    }

    [Fact]
    public void ImportAirports_VerificationSourceExceeds100_Fails()
    {
        var v = new ImportAirportsCommandValidator();
        AssertInvalid(v, new ImportAirportsCommand([
            new AirportImportDto("LHR", "EGLL", "Heathrow", "London", "GB", null, null, null,
                VerificationSource: new string('A', 101))]), "VerificationSource");
    }

    [Fact]
    public void ImportAirlines_VerifiedByExceeds255_Fails()
    {
        var v = new ImportAirlinesCommandValidator();
        AssertInvalid(v, new ImportAirlinesCommand([
            new AirlineImportDto("BA", null, "British Airways", "GB", null,
                VerifiedBy: new string('A', 256))]), "VerifiedBy");
    }

    [Fact]
    public void ImportAirlines_VerificationSourceExceeds100_Fails()
    {
        var v = new ImportAirlinesCommandValidator();
        AssertInvalid(v, new ImportAirlinesCommand([
            new AirlineImportDto("BA", null, "British Airways", "GB", null,
                VerificationSource: new string('A', 101))]), "VerificationSource");
    }

    [Fact]
    public void ImportLoungeProviders_VerifiedByExceeds255_Fails()
    {
        var v = new ImportLoungeProvidersCommandValidator();
        AssertInvalid(v, new ImportLoungeProvidersCommand([
            new LoungeProviderImportDto("AMEX", "American Express", "Card", null,
                VerifiedBy: new string('A', 256))]), "VerifiedBy");
    }

    [Fact]
    public void ImportLoungeProviders_VerificationSourceExceeds100_Fails()
    {
        var v = new ImportLoungeProvidersCommandValidator();
        AssertInvalid(v, new ImportLoungeProvidersCommand([
            new LoungeProviderImportDto("AMEX", "American Express", "Card", null,
                VerificationSource: new string('A', 101))]), "VerificationSource");
    }

    [Fact]
    public void ImportLoungeProviderKinds_VerifiedByExceeds255_Fails()
    {
        var v = new ImportLoyaltyProgramKindsCommandValidator();
        AssertInvalid(v, new ImportLoyaltyProgramKindsCommand([
            new LoyaltyProgramKindImportDto("AIRLINE", "Airline", null,
                VerifiedBy: new string('A', 256))]), "VerifiedBy");
    }

    [Fact]
    public void ImportLoyaltyProgramKinds_VerificationSourceExceeds100_Fails()
    {
        var v = new ImportLoyaltyProgramKindsCommandValidator();
        AssertInvalid(v, new ImportLoyaltyProgramKindsCommand([
            new LoyaltyProgramKindImportDto("AIRLINE", "Airline", null,
                VerificationSource: new string('A', 101))]), "VerificationSource");
    }

    [Fact]
    public void ImportLounges_VerifiedByExceeds255_Fails()
    {
        var v = new ImportLoungesCommandValidator();
        var dto = new LoungeImportDto("Test Lounge", "LHR", null, null, null, true, false, false, null, [],
            VerifiedBy: new string('A', 256));
        AssertInvalid(v, new ImportLoungesCommand([dto]), "VerifiedBy");
    }

    [Fact]
    public void ImportLounges_VerificationSourceExceeds100_Fails()
    {
        var v = new ImportLoungesCommandValidator();
        var dto = new LoungeImportDto("Test Lounge", "LHR", null, null, null, true, false, false, null, [],
            VerificationSource: new string('A', 101));
        AssertInvalid(v, new ImportLoungesCommand([dto]), "VerificationSource");
    }

    [Fact]
    public void ImportLoyaltyPrograms_VerifiedByExceeds255_Fails()
    {
        var v = new ImportLoyaltyProgramsCommandValidator();
        var dto = new LoyaltyProgramImportDto("BAEC", "Executive Club", "AIRLINE", "BA", [],
            VerifiedBy: new string('A', 256));
        AssertInvalid(v, new ImportLoyaltyProgramsCommand([dto]), "VerifiedBy");
    }

    [Fact]
    public void ImportLoyaltyPrograms_VerificationSourceExceeds100_Fails()
    {
        var v = new ImportLoyaltyProgramsCommandValidator();
        var dto = new LoyaltyProgramImportDto("BAEC", "Executive Club", "AIRLINE", "BA", [],
            VerificationSource: new string('A', 101));
        AssertInvalid(v, new ImportLoyaltyProgramsCommand([dto]), "VerificationSource");
    }

    // ── Admin: Delete commands ────────────────────────────────────────────────

    [Fact]
    public void DeleteAirline_Id0_Fails()
    {
        var v = new DeleteAirlineCommandValidator();
        AssertInvalid(v, new DeleteAirlineCommand(0), "Id");
    }

    [Fact]
    public void DeleteAirline_ValidId_Passes()
    {
        var v = new DeleteAirlineCommandValidator();
        AssertValid(v, new DeleteAirlineCommand(1));
    }

    [Fact]
    public void DeleteAirport_Id0_Fails()
    {
        var v = new DeleteAirportCommandValidator();
        AssertInvalid(v, new DeleteAirportCommand(0), "Id");
    }

    [Fact]
    public void DeleteLounge_Id0_Fails()
    {
        var v = new DeleteLoungeCommandValidator();
        AssertInvalid(v, new DeleteLoungeCommand(0), "Id");
    }

    [Fact]
    public void DeleteLoyaltyProgram_Id0_Fails()
    {
        var v = new DeleteLoyaltyProgramCommandValidator();
        AssertInvalid(v, new DeleteLoyaltyProgramCommand(0), "Id");
    }

    [Fact]
    public void DeleteTraveler_EmptyGuid_Fails()
    {
        var v = new DeleteTravelerCommandValidator();
        AssertInvalid(v, new DeleteTravelerCommand(Guid.Empty), "TravelerId");
    }

    [Fact]
    public void DeleteTraveler_ValidGuid_Passes()
    {
        var v = new DeleteTravelerCommandValidator();
        AssertValid(v, new DeleteTravelerCommand(Guid.NewGuid()));
    }

    // ── Flights: Untrack / Toggle / Sync / GetStatus ────────────────────────────

    [Fact]
    public void UntrackFlight_FlightId0_Fails()
    {
        var v = new UntrackFlightCommandValidator();
        AssertInvalid(v, new UntrackFlightCommand(Guid.NewGuid(), 0), "FlightId");
    }

    [Fact]
    public void UntrackFlight_ValidFlightId_Passes()
    {
        var v = new UntrackFlightCommandValidator();
        AssertValid(v, new UntrackFlightCommand(Guid.NewGuid(), 1));
    }

    [Fact]
    public void ToggleFlightNotification_FlightId0_Fails()
    {
        var v = new ToggleFlightNotificationCommandValidator();
        AssertInvalid(v, new ToggleFlightNotificationCommand(Guid.NewGuid(), 0, true), "FlightId");
    }

    [Fact]
    public void SyncFlightStatus_FlightId0_Fails()
    {
        var v = new SyncFlightStatusCommandValidator();
        AssertInvalid(v, new SyncFlightStatusCommand(Guid.NewGuid(), 0, SyncTrigger.Traveler), "FlightId");
    }

    [Fact]
    public void GetFlightStatus_FlightId0_Fails()
    {
        var v = new GetFlightStatusQueryValidator();
        AssertInvalid(v, new GetFlightStatusQuery(Guid.NewGuid(), 0), "FlightId");
    }

    [Fact]
    public void GetFlightStatus_ValidFlightId_Passes()
    {
        var v = new GetFlightStatusQueryValidator();
        AssertValid(v, new GetFlightStatusQuery(Guid.NewGuid(), 1));
    }

    // ── Loyalty: RemoveMembership ───────────────────────────────────────────────

    [Fact]
    public void RemoveLoyaltyMembership_EmptyMembershipId_Fails()
    {
        var v = new RemoveLoyaltyMembershipCommandValidator();
        AssertInvalid(v, new RemoveLoyaltyMembershipCommand(Guid.NewGuid(), Guid.Empty), "MembershipId");
    }

    [Fact]
    public void RemoveLoyaltyMembership_ValidMembershipId_Passes()
    {
        var v = new RemoveLoyaltyMembershipCommandValidator();
        AssertValid(v, new RemoveLoyaltyMembershipCommand(Guid.NewGuid(), Guid.NewGuid()));
    }

    // ── Case-insensitive code inputs (#265) ─────────────────────────────────────

    // ── FlightNumber: numeric-prefix carriers (#280 task 4) ─────────────────────
    // These carriers (IndiGo "6E", jetBlue-style numeric prefixes, etc.) are findable by search
    // but, before this fix, were rejected on POST because TrackFlightCommandValidator's regex
    // required an alphabetic prefix while SearchFlightsQueryValidator's did not.

    [Theory]
    [InlineData("6E4120")] // IndiGo — numeric-then-letter prefix
    [InlineData("U21234")] // easyJet — letter-then-digit prefix
    [InlineData("9W1234")] // Jet Airways — digit-then-letter prefix
    [InlineData("B61234")] // JetBlue — letter-then-digit prefix
    [InlineData("B1234")]  // "B"+"1" satisfies [A-Z][A-Z0-9] — a plausible designator shape
                            // (compare the real B6/U2/9W/6E); whether "B1" is an assigned
                            // carrier is reference data's job (TrackFlightCommandHandler's 404),
                            // not this regex's. Must stay valid.
    public void TrackFlight_NumericPrefixFlightNumbers_Pass(string flightNumber)
    {
        var v = new TrackFlightCommandValidator(TimeProvider.System);
        var cmd = ValidTrackFlightCommand() with { FlightNumber = flightNumber };
        AssertValid(v, cmd);
    }

    [Theory]
    [InlineData("123")]    // all-digit prefix "12" — no real IATA airline designator is all digits
    [InlineData("12345A")] // same, with a valid digit run and suffix — only the prefix is wrong
    public void TrackFlight_AllDigitPrefixFlightNumbers_Fail(string flightNumber)
    {
        var v = new TrackFlightCommandValidator(TimeProvider.System);
        var cmd = ValidTrackFlightCommand() with { FlightNumber = flightNumber };
        AssertInvalid(v, cmd, "FlightNumber");
    }

    [Theory]
    [InlineData("lhr")]
    [InlineData("jFk")]
    public void GetLoungesAtAirport_AnyCaseIata_Passes(string iata)
    {
        var v = new GetLoungesAtAirportQueryValidator();
        AssertValid(v, new GetLoungesAtAirportQuery(iata, null));
    }

    [Fact]
    public void GetLoungesAtAirport_WrongLength_Fails()
    {
        var v = new GetLoungesAtAirportQueryValidator();
        AssertInvalid(v, new GetLoungesAtAirportQuery("lh", null), "AirportIata");
    }

    [Fact]
    public void UpdateAirport_LowercaseCodes_Passes()
    {
        var v = new UpdateAirportCommandValidator();
        AssertValid(v, new UpdateAirportCommand(1, "lhr", "egll", "Heathrow", "London", "gb", null, null, null));
    }

    [Fact]
    public void UpdateAirline_LowercaseCodes_Passes()
    {
        var v = new UpdateAirlineCommandValidator();
        AssertValid(v, new UpdateAirlineCommand(1, "ba", "baw", "British Airways", "gb", null));
    }

    [Fact]
    public void ImportAirlines_LowercaseCodes_Passes()
    {
        var v = new ImportAirlinesCommandValidator();
        AssertValid(v, new ImportAirlinesCommand([
            new AirlineImportDto("ba", "baw", "British Airways", "gb", null)]));
    }

    [Fact]
    public void ImportLounges_LowercaseIata_Passes()
    {
        var v = new ImportLoungesCommandValidator();
        var dto = new LoungeImportDto("Test Lounge", "lhr", null, null, null, true, false, false, null, []);
        AssertValid(v, new ImportLoungesCommand([dto]));
    }

    [Fact]
    public void ImportLoyaltyPrograms_LowercaseAirlineIata_Passes()
    {
        var v = new ImportLoyaltyProgramsCommandValidator();
        var tier = new LoyaltyTierImportDto("GOLD", "Gold", 1, null, null);
        AssertValid(v, new ImportLoyaltyProgramsCommand([
            new LoyaltyProgramImportDto("BAEC", "Executive Club", "AIRLINE", "ba", [tier])]));
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private static TrackFlightCommand ValidTrackFlightCommand()
    {
        var now = DateTime.UtcNow;
        return new TrackFlightCommand(
            TravelerId: Guid.NewGuid(),
            FlightNumber: "BA123",
            ServiceDate: DateOnly.FromDateTime(now),
            DepartureAirportIata: "LHR",
            ArrivalAirportIata: "JFK",
            ScheduledDepartureUtc: now.AddHours(1),
            ScheduledArrivalUtc: now.AddHours(9));
    }

    /// <summary>Matches AeroApiOptions' own defaults (12 future / 3 past months) so validator
    /// edge cases here exercise the same bounds production actually uses.</summary>
    private sealed class FakeHorizonOptions : IFlightSearchHorizonOptions
    {
        public int MaxFutureMonths => 12;
        public int MaxPastMonths => 3;
    }

    private static readonly IFlightSearchHorizonOptions DefaultHorizon = new FakeHorizonOptions();
}
