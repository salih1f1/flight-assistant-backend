using CsvHelper.Configuration;
using FlightAssistant.Application.Commands.Admin.Import;
using System.Globalization;

namespace FlightAssistant.API.Csv;

// ── Flat entities ──────────────────────────────────────────────────────────────

public sealed class AllianceImportDtoMap : ClassMap<AllianceImportDto>
{
    public AllianceImportDtoMap() { AutoMap(CultureInfo.InvariantCulture); }
}

public sealed class CountryImportDtoMap : ClassMap<CountryImportDto>
{
    public CountryImportDtoMap() { AutoMap(CultureInfo.InvariantCulture); }
}

public sealed class AirportImportDtoMap : ClassMap<AirportImportDto>
{
    public AirportImportDtoMap() { AutoMap(CultureInfo.InvariantCulture); }
}

public sealed class AirlineImportDtoMap : ClassMap<AirlineImportDto>
{
    public AirlineImportDtoMap() { AutoMap(CultureInfo.InvariantCulture); }
}

public sealed class LoungeProviderImportDtoMap : ClassMap<LoungeProviderImportDto>
{
    public LoungeProviderImportDtoMap() { AutoMap(CultureInfo.InvariantCulture); }
}

public sealed class LoyaltyProgramKindImportDtoMap : ClassMap<LoyaltyProgramKindImportDto>
{
    public LoyaltyProgramKindImportDtoMap() { AutoMap(CultureInfo.InvariantCulture); }
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
    }
}
