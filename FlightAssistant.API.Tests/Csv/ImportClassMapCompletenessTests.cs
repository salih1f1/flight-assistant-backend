using System.Reflection;
using CsvHelper.Configuration;
using FlightAssistant.API.Csv;
using FlightAssistant.Application.Commands.Admin.Import;

namespace FlightAssistant.API.Tests.Csv;

/// <summary>
/// #362 fix round 1: the import <see cref="ClassMap"/>s map constructor
/// <see cref="ClassMap.Parameter(string)"/>s explicitly rather than <c>AutoMap</c> — AutoMap's own
/// safety net (every constructor parameter automatically gets a column) is gone as a result, so a
/// parameter added to an import DTO without a matching <c>Parameter(...)</c> call in its map
/// compiles fine and fails only at runtime, on the first real import.
///
/// <para>
/// #521: the sweep is <b>derived, not listed</b>. This file used to carry eight hand-written
/// <c>MemberData</c> rows while its own summary claimed it proved the rule "for all 8 maps" — true
/// only because someone remembered to add each row, so a ninth import map would have been covered
/// by nothing while this test went on passing. That is the #506 defect with the failure direction
/// reversed: #506 asserted an incidental property and went red loudly, this one would simply have
/// stopped testing anything, silently.
/// </para>
///
/// <para>
/// <b>The obligation rule</b> is not "is it one of the import maps I know about" but: <i>a map that
/// registers any parameter map at all must register every one of its row type's primary-constructor
/// parameters.</i> That is the condition under which losing AutoMap's net can actually bite, so it
/// is what a member is obliged by — and it needs no allowlist, no namespace coupling and no
/// maintenance.
/// </para>
///
/// <para>
/// <b>What that predicate actually sweeps, measured 2026-09-19</b> rather than assumed — the first
/// draft of this rewrite asserted in prose that the export maps were exempt "because they AutoMap",
/// and printing the real numbers showed that was wrong. Of the 16 <see cref="ClassMap"/>s in the API
/// assembly, <b>14 are obliged</b>: all 8 import maps (parameter maps only, no member maps), plus
/// <b>6 of the 8</b> export maps — <c>AutoMap</c> on a record <i>does</i> register constructor
/// parameters, so those six are swept in and satisfy the rule trivially. That is a free bonus, not
/// noise: an export map that ever partially loses AutoMap gets caught by the same assertion.
/// </para>
///
/// <para>
/// The <b>2 exempt export maps</b> are <c>LoungeExportDtoMap</c> and
/// <c>LoyaltyProgramExportDtoMap</c>, and the reason is structural: their row types each carry an
/// <c>IReadOnlyList&lt;&gt;</c> parameter (<c>Providers</c>, <c>Tiers</c>), which AutoMap cannot put
/// in a single CSV column, so it registers <i>no</i> parameter maps for them at all — 13 and 8
/// member maps instead — and the rule does not bind them. Also exempt, for a different and equally
/// structural reason, are the nested import DTOs <see cref="LoyaltyTierImportDto"/> and
/// <see cref="LoungeProviderLinkImportDto"/>: nothing parses them as a CSV row, so they have no
/// <see cref="ClassMap"/> for the sweep to find.
/// </para>
///
/// </summary>
public class ImportClassMapCompletenessTests
{
    /// <summary>
    /// Every concrete <see cref="ClassMap{T}"/> in the API assembly, paired with its row type — the
    /// whole search space, before the obligation rule narrows it.
    /// </summary>
    private static IEnumerable<(Type MapType, Type RowType)> AllClassMaps() =>
        typeof(AllianceImportDtoMap).Assembly.GetTypes()
            .Where(t => t is { IsClass: true, IsAbstract: false, IsGenericTypeDefinition: false })
            .Select(t => (MapType: t, RowType: RowTypeOf(t)))
            .Where(x => x.RowType is not null)
            .Select(x => (x.MapType, RowType: x.RowType!));

    /// <summary>The <c>T</c> of the nearest <see cref="ClassMap{T}"/> in the type's base chain.</summary>
    private static Type? RowTypeOf(Type type)
    {
        for (var t = type.BaseType; t is not null; t = t.BaseType)
            if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(ClassMap<>))
                return t.GetGenericArguments()[0];
        return null;
    }

    private static ClassMap Instantiate(Type mapType) => (ClassMap)Activator.CreateInstance(mapType)!;

    /// <summary>
    /// The maps the rule actually binds: those registering at least one parameter map. Instantiating
    /// is the only way to ask — a map's registrations happen in its constructor.
    /// </summary>
    private static IEnumerable<(Type MapType, Type RowType)> ObligedMaps() =>
        AllClassMaps().Where(x => Instantiate(x.MapType).ParameterMaps.Count > 0);

    public static TheoryData<Type, Type> MapsThatRegisterParameters()
    {
        var data = new TheoryData<Type, Type>();
        foreach (var (mapType, rowType) in ObligedMaps())
            data.Add(mapType, rowType);
        return data;
    }

    [Theory]
    [MemberData(nameof(MapsThatRegisterParameters))]
    public void AMapThatRegistersParameters_RegistersEveryConstructorParameterOfItsRowType(
        Type mapType, Type rowType)
    {
        var ctor = rowType.GetConstructors(BindingFlags.Public | BindingFlags.Instance).Single();
        var expected = ctor.GetParameters()
            .Select(p => p.Name!)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        var actual = Instantiate(mapType).ParameterMaps
            .Select(p => p.Data.Parameter.Name!)
            .OrderBy(n => n, StringComparer.Ordinal)
            .ToList();

        Assert.Equal(expected, actual);
    }

    // ── Counter-assertions ────────────────────────────────────────────────────
    // A narrowed sweep can silently match nothing, or silently stop matching the members that
    // mattered — in which case the Theory above passes by having no cases left to run. These two
    // are what stop that, and they are the part the hand-written list never had.

    [Fact]
    public void TheSweepStillMatchesSomething()
    {
        Assert.NotEmpty(AllClassMaps());
        Assert.NotEmpty(ObligedMaps());
    }

    /// <summary>
    /// The eight import maps are the members this guard was written for, so the derived predicate
    /// must still reach every one of them. Naming them is not a second hardcoded sweep — the sweep
    /// is derived above; this is a floor under it, which is exactly what the old list lacked.
    /// </summary>
    [Fact]
    public void EveryImportMapIsStillObligedByTheRule()
    {
        Type[] importMaps =
        [
            typeof(AllianceImportDtoMap),
            typeof(CountryImportDtoMap),
            typeof(AirportImportDtoMap),
            typeof(AirlineImportDtoMap),
            typeof(LoungeProviderImportDtoMap),
            typeof(LoyaltyProgramKindImportDtoMap),
            typeof(LoungeImportDtoMap),
            typeof(LoyaltyProgramImportDtoMap),
        ];

        var obliged = ObligedMaps().Select(x => x.MapType).ToList();

        foreach (var map in importMaps)
            Assert.Contains(map, obliged);
    }

    /// <summary>
    /// The nested import DTOs are exempt because nothing parses them as a CSV row, so no map exists
    /// for them. Pinned so that adding one later is a deliberate act rather than a silent gap: if
    /// either ever becomes a row type it gains a map, this fails, and whoever adds it has to decide
    /// whether the map registers parameters explicitly.
    /// </summary>
    [Theory]
    [InlineData(typeof(LoyaltyTierImportDto))]
    [InlineData(typeof(LoungeProviderLinkImportDto))]
    public void ANestedImportDtoHasNoClassMap(Type nestedDtoType) =>
        Assert.DoesNotContain(nestedDtoType, AllClassMaps().Select(x => x.RowType));
}
