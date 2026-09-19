using System.Globalization;
using CsvHelper.Configuration;
using FlightAssistant.Application.Queries.Admin.Export;

namespace FlightAssistant.API.Csv;

// ── Flat entities ──────────────────────────────────────────────────────────────

public sealed class AllianceExportDtoMap : ClassMap<AllianceExportDto>
{
    public AllianceExportDtoMap()
    {
        AutoMap(CultureInfo.InvariantCulture);
        this.MapExportVerificationColumns(m => m.VerifiedAt, m => m.VerifiedBy, m => m.VerificationSource);
    }
}

public sealed class CountryExportDtoMap : ClassMap<CountryExportDto>
{
    public CountryExportDtoMap()
    {
        AutoMap(CultureInfo.InvariantCulture);
        this.MapExportVerificationColumns(m => m.VerifiedAt, m => m.VerifiedBy, m => m.VerificationSource);
    }
}

public sealed class AirportExportDtoMap : ClassMap<AirportExportDto>
{
    public AirportExportDtoMap()
    {
        AutoMap(CultureInfo.InvariantCulture);
        this.MapExportVerificationColumns(m => m.VerifiedAt, m => m.VerifiedBy, m => m.VerificationSource);
    }
}

public sealed class AirlineExportDtoMap : ClassMap<AirlineExportDto>
{
    public AirlineExportDtoMap()
    {
        AutoMap(CultureInfo.InvariantCulture);
        this.MapExportVerificationColumns(m => m.VerifiedAt, m => m.VerifiedBy, m => m.VerificationSource);
    }
}

public sealed class LoungeProviderExportDtoMap : ClassMap<LoungeProviderExportDto>
{
    public LoungeProviderExportDtoMap()
    {
        AutoMap(CultureInfo.InvariantCulture);
        this.MapExportVerificationColumns(m => m.VerifiedAt, m => m.VerifiedBy, m => m.VerificationSource);
    }
}

public sealed class LoyaltyProgramKindExportDtoMap : ClassMap<LoyaltyProgramKindExportDto>
{
    public LoyaltyProgramKindExportDtoMap()
    {
        AutoMap(CultureInfo.InvariantCulture);
        this.MapExportVerificationColumns(m => m.VerifiedAt, m => m.VerifiedBy, m => m.VerificationSource);
    }
}

// ── Nested entities ────────────────────────────────────────────────────────────

public sealed class LoungeExportDtoMap : ClassMap<LoungeExportDto>
{
    public LoungeExportDtoMap()
    {
        Map(m => m.Name);
        Map(m => m.AirportIata);
        Map(m => m.Terminal);
        Map(m => m.LocationHint);
        Map(m => m.Hours);
        Map(m => m.IsAirside);
        Map(m => m.IsDomesticOnly);
        Map(m => m.IsInternationalOnly);
        Map(m => m.Amenities);
        Map(m => m.Providers).TypeConverter(CsvExportWriter.JsonCollection);
        this.MapExportVerificationColumns(m => m.VerifiedAt, m => m.VerifiedBy, m => m.VerificationSource);
    }
}

public sealed class LoyaltyProgramExportDtoMap : ClassMap<LoyaltyProgramExportDto>
{
    public LoyaltyProgramExportDtoMap()
    {
        Map(m => m.Code);
        Map(m => m.Name);
        Map(m => m.KindCode);
        Map(m => m.AirlineIata);
        Map(m => m.Tiers).TypeConverter(CsvExportWriter.JsonCollection);
        this.MapExportVerificationColumns(m => m.VerifiedAt, m => m.VerifiedBy, m => m.VerificationSource);
    }
}
