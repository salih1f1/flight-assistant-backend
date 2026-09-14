using CsvHelper.Configuration;
using FlightAssistant.Application.Commands.Admin.Import;

namespace FlightAssistant.API.Csv;

// ── Flat entities ──────────────────────────────────────────────────────────────
//
// Every import DTO below is a positional record with no parameterless constructor, so CsvHelper
// always builds it through the constructor. AutoMap over such a type registers one
// ClassMap.Parameter(name) per constructor parameter — and re-mapping just the three optional
// verification parameters (#362) on top of an AutoMap call does not replace AutoMap's own
// registration for that same name the way Map() does for a property; it *adds* a second,
// still-required Parameter entry, which both fails header validation on an older CSV (the
// AutoMap copy is still mandatory) and, once a CSV does carry the column, feeds CsvHelper's
// generated constructor call more arguments than the constructor actually takes. So these maps
// are explicit — every constructor parameter, verification columns included — rather than
// AutoMap-plus-override.

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
