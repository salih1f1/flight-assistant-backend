using CsvHelper.Configuration;
using FlightAssistant.Application.Commands.Admin.Import;

namespace FlightAssistant.API.Csv;

// ── Flat entities ──────────────────────────────────────────────────────────────
//
// Every import DTO below is a positional record with no parameterless constructor, so CsvHelper
// always builds it through the constructor. AutoMap over such a type registers one
// ClassMap.Parameter(name) per constructor parameter, non-optional by default — and #362 fix
// round 1 tried finding and mutating those three AutoMap-registered entries in place
// (Optional()/DateTimeStyles, even .Name(...) too) rather than adding fresh ones. That mutation is
// provably inert: CsvHelper 33.1.0's AutoMap leaves each auto-registered ParameterMap's
// Data.Names empty (confirmed by inspection — printing it after AutoMap shows `[]` for every
// parameter, including the ones that resolve to "Code"/"Name" as their header names without any
// override), and header validation for a constructor parameter — CsvReader.ValidateHeader(ClassMap,
// List<InvalidHeader>) — evaluates GetFieldIndex(Data.Names, ...), which cannot resolve a header
// from an empty Names collection regardless of Data.IsOptional. A minimal repro outside this
// codebase (AutoMap a record, then `ParameterMaps.Single(p => ...).Name(n).Optional()` on the
// found entry) reproduces the exact same HeaderValidationException even with IsOptional
// confirmed true and Name explicitly set on the same object right before parsing — so this is a
// CsvHelper limitation for constructor-parameter mapping, not a mistake in how it was called here.
// The fully explicit Parameter(...) registration below — proven correct by the same repro — is
// the working alternative: registering fresh Parameter(name) entries (rather than reusing
// AutoMap's) for every constructor parameter, verification columns included.

public sealed class AllianceImportDtoMap : ClassMap<AllianceImportDto>
{
    public AllianceImportDtoMap()
    {
        Parameter("Code").Name("Code");
        Parameter("Name").Name("Name");
        this.MapImportVerificationParameters();
    }
}

public sealed class CountryImportDtoMap : ClassMap<CountryImportDto>
{
    public CountryImportDtoMap()
    {
        Parameter("Code").Name("Code");
        Parameter("Name").Name("Name");
        Parameter("Alpha3").Name("Alpha3");
        Parameter("NumericCode").Name("NumericCode");
        Parameter("Region").Name("Region");
        Parameter("Subregion").Name("Subregion");
        Parameter("IsEU").Name("IsEU");
        Parameter("IsSchengen").Name("IsSchengen");
        Parameter("IsEEA").Name("IsEEA");
        this.MapImportVerificationParameters();
    }
}

public sealed class AirportImportDtoMap : ClassMap<AirportImportDto>
{
    public AirportImportDtoMap()
    {
        Parameter("Iata").Name("Iata");
        Parameter("Icao").Name("Icao");
        Parameter("Name").Name("Name");
        Parameter("City").Name("City");
        Parameter("CountryCode").Name("CountryCode");
        Parameter("Latitude").Name("Latitude");
        Parameter("Longitude").Name("Longitude");
        Parameter("ElevationFt").Name("ElevationFt");
        this.MapImportVerificationParameters();
    }
}

public sealed class AirlineImportDtoMap : ClassMap<AirlineImportDto>
{
    public AirlineImportDtoMap()
    {
        Parameter("Iata").Name("Iata");
        Parameter("Icao").Name("Icao");
        Parameter("Name").Name("Name");
        Parameter("CountryCode").Name("CountryCode");
        Parameter("AllianceCode").Name("AllianceCode");
        this.MapImportVerificationParameters();
    }
}

public sealed class LoungeProviderImportDtoMap : ClassMap<LoungeProviderImportDto>
{
    public LoungeProviderImportDtoMap()
    {
        Parameter("Code").Name("Code");
        Parameter("Name").Name("Name");
        Parameter("Kind").Name("Kind");
        Parameter("Description").Name("Description");
        this.MapImportVerificationParameters();
    }
}

public sealed class LoyaltyProgramKindImportDtoMap : ClassMap<LoyaltyProgramKindImportDto>
{
    public LoyaltyProgramKindImportDtoMap()
    {
        Parameter("Code").Name("Code");
        Parameter("Name").Name("Name");
        Parameter("Description").Name("Description");
        this.MapImportVerificationParameters();
    }
}

// ── Nested entities (JSON columns for collections) ─────────────────────────────

public sealed class LoungeImportDtoMap : ClassMap<LoungeImportDto>
{
    public LoungeImportDtoMap()
    {
        Parameter("Name").Name("Name");
        Parameter("AirportIata").Name("AirportIata");
        Parameter("Terminal").Name("Terminal");
        Parameter("LocationHint").Name("LocationHint");
        Parameter("Hours").Name("Hours");
        Parameter("IsAirside").Name("IsAirside").Default(false);
        Parameter("IsDomesticOnly").Name("IsDomesticOnly").Default(false);
        Parameter("IsInternationalOnly").Name("IsInternationalOnly").Default(false);
        Parameter("Amenities").Name("Amenities");
        Parameter("Providers").Name("Providers")
            .TypeConverter(CsvImportParser.JsonListOf(typeof(IReadOnlyList<LoungeProviderLinkImportDto>)));
        this.MapImportVerificationParameters();
    }
}

public sealed class LoyaltyProgramImportDtoMap : ClassMap<LoyaltyProgramImportDto>
{
    public LoyaltyProgramImportDtoMap()
    {
        Parameter("Code").Name("Code");
        Parameter("Name").Name("Name");
        Parameter("KindCode").Name("KindCode");
        Parameter("AirlineIata").Name("AirlineIata");
        Parameter("AllianceCode").Name("AllianceCode");
        Parameter("Tiers").Name("Tiers")
            .TypeConverter(CsvImportParser.JsonListOf(typeof(IReadOnlyList<LoyaltyTierImportDto>)));
        this.MapImportVerificationParameters();
    }
}
