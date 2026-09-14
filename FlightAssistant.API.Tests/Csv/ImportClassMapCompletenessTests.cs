using System.Reflection;
using CsvHelper.Configuration;
using FlightAssistant.API.Csv;
using FlightAssistant.Application.Commands.Admin.Import;

namespace FlightAssistant.API.Tests.Csv;

/// <summary>
/// #362 fix round 1: every import <see cref="ClassMap"/> maps constructor
/// <see cref="ClassMap.Parameter(string)"/>s explicitly rather than <c>AutoMap</c> — AutoMap's own
/// safety net (every constructor parameter automatically gets a column) is gone as a result, so a
/// future parameter added to an import DTO without a matching <c>Parameter(...)</c> call in its map
/// would compile fine and only fail at runtime, on the first real import. This proves, for all 8
/// maps, that the map's registered parameter names are exactly the DTO's primary-constructor
/// parameter names — no more, no fewer.
/// </summary>
public class ImportClassMapCompletenessTests
{
    public static IEnumerable<object[]> Maps()
    {
        yield return new object[] { typeof(AllianceImportDto), typeof(AllianceImportDtoMap) };
        yield return new object[] { typeof(CountryImportDto), typeof(CountryImportDtoMap) };
        yield return new object[] { typeof(AirportImportDto), typeof(AirportImportDtoMap) };
        yield return new object[] { typeof(AirlineImportDto), typeof(AirlineImportDtoMap) };
        yield return new object[] { typeof(LoungeProviderImportDto), typeof(LoungeProviderImportDtoMap) };
        yield return new object[] { typeof(LoyaltyProgramKindImportDto), typeof(LoyaltyProgramKindImportDtoMap) };
        yield return new object[] { typeof(LoungeImportDto), typeof(LoungeImportDtoMap) };
        yield return new object[] { typeof(LoyaltyProgramImportDto), typeof(LoyaltyProgramImportDtoMap) };
    }

    [Theory]
    [MemberData(nameof(Maps))]
    public void Map_ParameterNames_MatchDtoPrimaryConstructorParameterNames(Type dtoType, Type mapType)
    {
        var ctor = dtoType.GetConstructors(BindingFlags.Public | BindingFlags.Instance).Single();
        var ctorParamNames = ctor.GetParameters()
            .Select(p => p.Name!)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        var map = (ClassMap)Activator.CreateInstance(mapType)!;
        var mapParamNames = map.ParameterMaps
            .Select(p => p.Data.Parameter.Name!)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(ctorParamNames, mapParamNames);
    }
}
