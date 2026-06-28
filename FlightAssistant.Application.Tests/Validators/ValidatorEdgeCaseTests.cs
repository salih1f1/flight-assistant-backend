using FlightAssistant.Application.Commands.Flights;
using FlightAssistant.Application.Commands.Loyalty;
using FlightAssistant.Application.Commands.Travelers;
using FlightAssistant.Application.Queries.Flights;
using FlightAssistant.Application.Validators;
using FlightAssistant.Application.Commands.Admin.Airlines;
using FlightAssistant.Application.Commands.Admin.Airports;
using FlightAssistant.Application.Commands.Admin.Lounges;
using FlightAssistant.Application.Commands.Admin.Loyalty;
using FlightAssistant.Application.Commands.Admin.Import;
using FlightAssistant.Application.Commands.Admin.Travelers;
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

    // ── Import: Alliances ─────────────────────────────────────────────────────────

    [Fact]
    public void ImportAlliances_EmptyCode_Fails()
    {
        var v = new ImportAlliancesCommandValidator();
        AssertInvalid(v, new ImportAlliancesCommand([new AllianceImportDto("", "Star Alliance")]), "Code");
    }

    [Fact]
    public void ImportAlliances_LowercaseCode_Fails()
    {
        var v = new ImportAlliancesCommandValidator();
        AssertInvalid(v, new ImportAlliancesCommand([new AllianceImportDto("sa", "Star Alliance")]), "Code");
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
    public void ImportCountries_LowercaseCode_Fails()
    {
        var v = new ImportCountriesCommandValidator();
        AssertInvalid(v, new ImportCountriesCommand([new CountryImportDto("gb", "UK", null, null, null, null, null, null, null)]), "Code");
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
            new AirportImportDto("LHRX", null, "Heathrow", "London", "GB", null, null, null, null)]), "Iata");
    }

    [Fact]
    public void ImportAirports_LowercaseIata_Fails()
    {
        var v = new ImportAirportsCommandValidator();
        AssertInvalid(v, new ImportAirportsCommand([
            new AirportImportDto("lhr", null, "Heathrow", "London", "GB", null, null, null, null)]), "Iata");
    }

    [Fact]
    public void ImportAirports_ValidItem_Passes()
    {
        var v = new ImportAirportsCommandValidator();
        AssertValid(v, new ImportAirportsCommand([
            new AirportImportDto("LHR", "EGLL", "Heathrow", "London", "GB", 51.477, -0.461, 83, "Europe/London")]));
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
            new LoyaltyProgramImportDto(new string('A', 17), "Club", "AIRLINE", null, null, [])]), "Code");
    }

    [Fact]
    public void ImportLoyaltyPrograms_TierWithZeroRank_Fails()
    {
        var v = new ImportLoyaltyProgramsCommandValidator();
        var tier = new LoyaltyTierImportDto("GOLD", "Gold", 0, null, null);
        AssertInvalid(v, new ImportLoyaltyProgramsCommand([
            new LoyaltyProgramImportDto("BAEC", "Executive Club", "AIRLINE", null, null, [tier])]), "Rank");
    }

    [Fact]
    public void ImportLoyaltyPrograms_ValidItemWithTiers_Passes()
    {
        var v = new ImportLoyaltyProgramsCommandValidator();
        var tier = new LoyaltyTierImportDto("GOLD", "Gold", 1, null, null);
        AssertValid(v, new ImportLoyaltyProgramsCommand([
            new LoyaltyProgramImportDto("BAEC", "Executive Club", "AIRLINE", "BA", null, [tier])]));
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
        AssertInvalid(v, new SyncFlightStatusCommand(Guid.NewGuid(), 0), "FlightId");
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
