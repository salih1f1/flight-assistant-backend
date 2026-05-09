using FlightAssistant.Application.Commands.Flights;
using FlightAssistant.Application.Commands.Loyalty;
using FlightAssistant.Application.Commands.Travelers;
using FlightAssistant.Application.Queries.Flights;
using FlightAssistant.Application.Queries.Visa;
using FlightAssistant.Application.Validators;
using FlightAssistant.Application.Validators.Admin;
using FlightAssistant.Application.Commands.Admin.Airlines;
using FlightAssistant.Application.Commands.Admin.Airports;
using FlightAssistant.Application.Commands.Admin.Lounges;
using FlightAssistant.Application.Commands.Admin.Loyalty;
using FlightAssistant.Contracts.Paging;
using FluentValidation;

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
    public void TrackFlight_ValidFlightNumbers_Pass(string flightNumber)
    {
        var v = new TrackFlightCommandValidator();
        var cmd = ValidTrackFlightCommand() with { FlightNumber = flightNumber };
        AssertValid(v, cmd);
    }

    [Theory]
    [InlineData("ba123")]      // lowercase
    [InlineData("BA12345")]    // 5 digits
    [InlineData("B1234")]      // single letter prefix
    [InlineData("123")]        // no letters
    [InlineData("")]           // empty
    public void TrackFlight_InvalidFlightNumbers_Fail(string flightNumber)
    {
        var v = new TrackFlightCommandValidator();
        var cmd = ValidTrackFlightCommand() with { FlightNumber = flightNumber };
        AssertInvalid(v, cmd, "FlightNumber");
    }

    [Fact]
    public void TrackFlight_PastServiceDate_Fails()
    {
        var v = new TrackFlightCommandValidator();
        var cmd = ValidTrackFlightCommand() with
        {
            ServiceDate = DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-1))
        };
        AssertInvalid(v, cmd, "ServiceDate");
    }

    [Fact]
    public void TrackFlight_ArrivalBeforeDeparture_Fails()
    {
        var v = new TrackFlightCommandValidator();
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
    public void TrackFlight_ValidAirportIata_Pass(string iata)
    {
        var v = new TrackFlightCommandValidator();
        var cmd = ValidTrackFlightCommand() with { DepartureAirportIata = iata };
        AssertValid(v, cmd);
    }

    [Theory]
    [InlineData("lhr")]    // lowercase
    [InlineData("LHRX")]   // 4 chars
    [InlineData("LH")]     // 2 chars
    [InlineData("1LH")]    // digit prefix
    public void TrackFlight_InvalidAirportIata_Fail(string iata)
    {
        var v = new TrackFlightCommandValidator();
        var cmd = ValidTrackFlightCommand() with { DepartureAirportIata = iata };
        AssertInvalid(v, cmd, "DepartureAirportIata");
    }

    // ── Country codes ─────────────────────────────────────────────────────────

    [Theory]
    [InlineData("GB")]
    [InlineData("US")]
    public void RegisterTraveler_ValidCountryCode_Passes(string code)
    {
        var v = new RegisterTravelerCommandValidator();
        var cmd = new RegisterTravelerCommand(Guid.NewGuid(), "test@example.com", null, code);
        AssertValid(v, cmd);
    }

    [Theory]
    [InlineData("gb")]     // lowercase
    [InlineData("GBR")]    // alpha-3
    [InlineData("G1")]     // digit
    public void RegisterTraveler_InvalidCountryCode_Fails(string code)
    {
        var v = new RegisterTravelerCommandValidator();
        var cmd = new RegisterTravelerCommand(Guid.NewGuid(), "test@example.com", null, code);
        AssertInvalid(v, cmd, "CountryCode");
    }

    [Fact]
    public void RegisterTraveler_NullCountryCode_Passes()
    {
        var v = new RegisterTravelerCommandValidator();
        var cmd = new RegisterTravelerCommand(Guid.NewGuid(), "test@example.com", null, null);
        AssertValid(v, cmd);
    }

    // ── Email ─────────────────────────────────────────────────────────────────

    [Fact]
    public void RegisterTraveler_ValidEmail_Passes()
    {
        var v = new RegisterTravelerCommandValidator();
        AssertValid(v, new RegisterTravelerCommand(Guid.NewGuid(), "user@example.com", null, null));
    }

    [Fact]
    public void RegisterTraveler_EmailExceeds254Chars_Fails()
    {
        var v = new RegisterTravelerCommandValidator();
        var longEmail = new string('a', 249) + "@b.com"; // 255 chars — exceeds 254
        AssertInvalid(v, new RegisterTravelerCommand(Guid.NewGuid(), longEmail, null, null), "Email");
    }

    [Theory]
    [InlineData("notanemail")]
    [InlineData("@nodomain")]
    [InlineData("")]
    public void RegisterTraveler_InvalidEmail_Fails(string email)
    {
        var v = new RegisterTravelerCommandValidator();
        AssertInvalid(v, new RegisterTravelerCommand(Guid.NewGuid(), email, null, null), "Email");
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

    // ── Visa query ────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("GB", "JP")]
    [InlineData("US", "DE")]
    public void VisaQuery_ValidCodes_Pass(string passport, string destination)
    {
        var v = new GetVisaRequirementsQueryValidator();
        AssertValid(v, new GetVisaRequirementsQuery(passport, destination));
    }

    [Theory]
    [InlineData("gb", "JP")]   // lowercase passport
    [InlineData("GB", "jp")]   // lowercase destination
    [InlineData("GBR", "JP")]  // alpha-3
    [InlineData("", "JP")]     // empty
    public void VisaQuery_InvalidCodes_Fail(string passport, string destination)
    {
        var v = new GetVisaRequirementsQueryValidator();
        AssertInvalid(v, new GetVisaRequirementsQuery(passport, destination));
    }

    // ── Loyalty membership ────────────────────────────────────────────────────

    [Fact]
    public void AddMembership_AccountNumberExceeds64_Fails()
    {
        var v = new AddLoyaltyMembershipCommandValidator();
        var cmd = new AddLoyaltyMembershipCommand(Guid.NewGuid(), 1, new string('A', 65), null, null, null);
        AssertInvalid(v, cmd, "AccountNumber");
    }

    [Fact]
    public void AddMembership_AccountNumber64Chars_Passes()
    {
        var v = new AddLoyaltyMembershipCommandValidator();
        var cmd = new AddLoyaltyMembershipCommand(Guid.NewGuid(), 1, new string('A', 64), null, null, null);
        AssertValid(v, cmd);
    }

    [Fact]
    public void AddMembership_PastTierExpiry_Fails()
    {
        var v = new AddLoyaltyMembershipCommandValidator();
        var cmd = new AddLoyaltyMembershipCommand(Guid.NewGuid(), 1, "ACC1", null,
            DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-1)), null);
        AssertInvalid(v, cmd, "TierExpiryDate");
    }

    [Fact]
    public void AddMembership_FutureTierExpiry_Passes()
    {
        var v = new AddLoyaltyMembershipCommandValidator();
        var cmd = new AddLoyaltyMembershipCommand(Guid.NewGuid(), 1, "ACC1", null,
            DateOnly.FromDateTime(DateTime.UtcNow.AddDays(30)), null);
        AssertValid(v, cmd);
    }

    [Fact]
    public void AddMembership_NullExpiryDates_Passes()
    {
        var v = new AddLoyaltyMembershipCommandValidator();
        var cmd = new AddLoyaltyMembershipCommand(Guid.NewGuid(), 1, "ACC1", null, null, null);
        AssertValid(v, cmd);
    }

    [Fact]
    public void UpdateMembership_PastPointsExpiry_Fails()
    {
        var v = new UpdateLoyaltyMembershipCommandValidator();
        var cmd = new UpdateLoyaltyMembershipCommand(Guid.NewGuid(), Guid.NewGuid(), "ACC1", null,
            null, DateOnly.FromDateTime(DateTime.UtcNow.AddDays(-1)));
        AssertInvalid(v, cmd, "PointsExpiryDate");
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
    public void CreateAirline_LowercaseIata_Fails()
    {
        var v = new CreateAirlineCommandValidator();
        AssertInvalid(v, new CreateAirlineCommand("ba", null, "British Airways", "GB", null), "Iata");
    }

    [Fact]
    public void CreateAirline_InvalidCountryCode_Fails()
    {
        var v = new CreateAirlineCommandValidator();
        AssertInvalid(v, new CreateAirlineCommand("BA", null, "British Airways", "GBR", null), "CountryCode");
    }

    // ── Admin: Airport ────────────────────────────────────────────────────────

    [Fact]
    public void CreateAirport_LowercaseIata_Fails()
    {
        var v = new CreateAirportCommandValidator();
        AssertInvalid(v, new CreateAirportCommand("lhr", null, "Heathrow", "London", "GB", null, null, null, null), "Iata");
    }

    [Fact]
    public void CreateAirport_CityExceeds200_Fails()
    {
        var v = new CreateAirportCommandValidator();
        AssertInvalid(v, new CreateAirportCommand("LHR", null, "Heathrow", new string('A', 201), "GB", null, null, null, null), "City");
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
        AssertInvalid(v, new CreateLoyaltyProgramCommand(new string('A', 17), "BA Executive Club", 1, null, null), "Code");
    }

    [Fact]
    public void CreateLoyaltyProgram_AllianceCodeExceeds8_Fails()
    {
        var v = new CreateLoyaltyProgramCommandValidator();
        AssertInvalid(v, new CreateLoyaltyProgramCommand("BAEC", "BA Executive Club", 1, null, "TOOLONGCODE"), "AllianceCode");
    }

    // ── Flight search ─────────────────────────────────────────────────────────

    [Fact]
    public void SearchFlights_ValidQuery_Passes()
    {
        var v = new SearchFlightsQueryValidator();
        AssertValid(v, new SearchFlightsQuery("LHR", "JFK", DateOnly.FromDateTime(DateTime.UtcNow)));
    }

    [Theory]
    [InlineData("lhr", "JFK")] // lowercase From
    [InlineData("LH", "JFK")]  // 2-char From
    [InlineData("LHR", "jfk")] // lowercase To
    public void SearchFlights_InvalidIata_Fails(string from, string to)
    {
        var v = new SearchFlightsQueryValidator();
        AssertInvalid(v, new SearchFlightsQuery(from, to, DateOnly.FromDateTime(DateTime.UtcNow)));
    }

    // ── Private helpers ───────────────────────────────────────────────────────

    private static TrackFlightCommand ValidTrackFlightCommand()
    {
        var now = DateTime.UtcNow;
        return new TrackFlightCommand(
            TravelerId: Guid.NewGuid(),
            AirlineIata: "BA",
            FlightNumber: "BA123",
            ServiceDate: DateOnly.FromDateTime(now),
            DepartureAirportIata: "LHR",
            ArrivalAirportIata: "JFK",
            ScheduledDepartureUtc: now.AddHours(1),
            ScheduledArrivalUtc: now.AddHours(9));
    }
}
