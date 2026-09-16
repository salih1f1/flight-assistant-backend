using System.Globalization;
using FlightAssistant.Domain.ReferenceData.Entities;
using FlightAssistant.Domain.Verification.Enums;

namespace FlightAssistant.Application.Verification
{
    /// <summary>
    /// Which matching rule <see cref="VerificationComparer"/> applies to a checked column
    /// (spec §3.1). Never persisted -- unrelated to <c>VerificationRequestStatus</c> (Domain),
    /// which is the request's own stored outcome, not a column's matching rule.
    /// </summary>
    public enum VerificationColumnKind
    {
        /// <summary>Iata, Icao, CountryCode, Country.Code, Alpha3: exact match after trimming
        /// and upper-casing. See <see cref="VerificationValueNormalizer.NormalizeCode"/>.</summary>
        Code,

        /// <summary>Name, City, Region, Subregion: match after accent/case/punctuation/whitespace
        /// -insensitive normalisation. See <see cref="VerificationValueNormalizer.NormalizeText"/>.</summary>
        Text,

        /// <summary>Country.NumericCode: compared as an integer, so <c>"004"</c> equals <c>4</c>.
        /// See <see cref="VerificationValueNormalizer.TryParseNumericCode"/>.</summary>
        NumericCode,

        /// <summary>Airport.Latitude / Airport.Longitude: equal within
        /// <see cref="VerificationComparer.CoordinateToleranceDegrees"/> degrees.</summary>
        Coordinate,

        /// <summary>Airport.ElevationFt: equal within <see cref="VerificationComparer.ElevationToleranceFt"/> feet.</summary>
        Elevation
    }

    /// <summary>One checked column: the name used as the key in "ours"/source records and in
    /// <c>differences</c>/<c>source_values</c> (matches the entity's C# property name), and which
    /// matching rule applies to it.</summary>
    public sealed record VerificationColumnDefinition(string Name, VerificationColumnKind Kind);

    /// <summary>
    /// The one registry of which columns an automated check compares per entity type (spec §3),
    /// and the only place that turns one of our own <see cref="Airport"/>/<see cref="Airline"/>/
    /// <see cref="Country"/> rows into the same <c>column name -&gt; value</c> shape a source hands
    /// back (<see cref="VerificationSourceLookupResult.Found"/>). Driving both
    /// <see cref="GetColumns"/> and the <c>Project</c> overloads from the same column list is what
    /// stops a column being checked on one side and forgotten on the other: a column cannot be
    /// added here without also supplying the selector that reads it off the entity.
    ///
    /// <para>
    /// <c>Airline.AllianceCode</c>, <c>Airline.CountryCode</c> (#401, R1) and
    /// <c>Country.IsEU</c>/<c>IsSchengen</c>/<c>IsEEA</c> are deliberately absent --
    /// <see cref="VerificationComparer"/> only ever looks at columns this registry lists, so an
    /// unregistered column is silently ignored rather than needing an explicit exclusion list
    /// anywhere. <c>CountryCode</c> came out of the airline list because <c>aeroapi</c> -- the
    /// airline source -- reports an operator's country as a name (<c>"United Kingdom"</c>), never a
    /// code, and nothing in the app reads an airline's own country; it stays on <c>Airport</c>,
    /// where every source reports a real two-letter code.
    /// </para>
    /// </summary>
    public static class VerificationCheckedColumns
    {
        private static readonly IReadOnlyList<(VerificationColumnDefinition Column, Func<Airport, string?> Select)> AirportColumns =
            new (VerificationColumnDefinition, Func<Airport, string?>)[]
            {
                (new VerificationColumnDefinition(nameof(Airport.Iata), VerificationColumnKind.Code), a => a.Iata),
                (new VerificationColumnDefinition(nameof(Airport.Icao), VerificationColumnKind.Code), a => a.Icao),
                (new VerificationColumnDefinition(nameof(Airport.Name), VerificationColumnKind.Text), a => a.Name),
                (new VerificationColumnDefinition(nameof(Airport.City), VerificationColumnKind.Text), a => a.City),
                (new VerificationColumnDefinition(nameof(Airport.CountryCode), VerificationColumnKind.Code), a => a.CountryCode),
                (new VerificationColumnDefinition(nameof(Airport.Latitude), VerificationColumnKind.Coordinate), a => FormatNumber(a.Latitude)),
                (new VerificationColumnDefinition(nameof(Airport.Longitude), VerificationColumnKind.Coordinate), a => FormatNumber(a.Longitude)),
                (new VerificationColumnDefinition(nameof(Airport.ElevationFt), VerificationColumnKind.Elevation), a => FormatNumber(a.ElevationFt))
            };

        private static readonly IReadOnlyList<(VerificationColumnDefinition Column, Func<Airline, string?> Select)> AirlineColumns =
            new (VerificationColumnDefinition, Func<Airline, string?>)[]
            {
                (new VerificationColumnDefinition(nameof(Airline.Iata), VerificationColumnKind.Code), a => a.Iata),
                (new VerificationColumnDefinition(nameof(Airline.Icao), VerificationColumnKind.Code), a => a.Icao),
                (new VerificationColumnDefinition(nameof(Airline.Name), VerificationColumnKind.Text), a => a.Name)
            };

        private static readonly IReadOnlyList<(VerificationColumnDefinition Column, Func<Country, string?> Select)> CountryColumns =
            new (VerificationColumnDefinition, Func<Country, string?>)[]
            {
                (new VerificationColumnDefinition(nameof(Country.Code), VerificationColumnKind.Code), c => c.Code),
                (new VerificationColumnDefinition(nameof(Country.Name), VerificationColumnKind.Text), c => c.Name),
                (new VerificationColumnDefinition(nameof(Country.Alpha3), VerificationColumnKind.Code), c => c.Alpha3),
                (new VerificationColumnDefinition(nameof(Country.NumericCode), VerificationColumnKind.NumericCode), c => FormatNumber(c.NumericCode)),
                (new VerificationColumnDefinition(nameof(Country.Region), VerificationColumnKind.Text), c => c.Region),
                (new VerificationColumnDefinition(nameof(Country.Subregion), VerificationColumnKind.Text), c => c.Subregion)
            };

        /// <summary>The checked columns for <paramref name="entityType"/>, in a fixed order --
        /// the same order <see cref="VerificationComparer"/> reports a source's differing columns in.</summary>
        public static IReadOnlyList<VerificationColumnDefinition> GetColumns(VerificationEntityType entityType) =>
            entityType switch
            {
                VerificationEntityType.Airport => AirportColumns.Select(c => c.Column).ToList(),
                VerificationEntityType.Airline => AirlineColumns.Select(c => c.Column).ToList(),
                VerificationEntityType.Country => CountryColumns.Select(c => c.Column).ToList(),
                _ => throw new ArgumentOutOfRangeException(nameof(entityType), entityType, "Unknown entity type.")
            };

        /// <summary>Our own checked-column values for <paramref name="airport"/>, keyed by the
        /// same column names <see cref="GetColumns"/> returns for <see cref="VerificationEntityType.Airport"/>.</summary>
        public static IReadOnlyDictionary<string, string?> Project(Airport airport) => Project(AirportColumns, airport);

        /// <summary>Our own checked-column values for <paramref name="airline"/>, keyed by the
        /// same column names <see cref="GetColumns"/> returns for <see cref="VerificationEntityType.Airline"/>.</summary>
        public static IReadOnlyDictionary<string, string?> Project(Airline airline) => Project(AirlineColumns, airline);

        /// <summary>Our own checked-column values for <paramref name="country"/>, keyed by the
        /// same column names <see cref="GetColumns"/> returns for <see cref="VerificationEntityType.Country"/>.</summary>
        public static IReadOnlyDictionary<string, string?> Project(Country country) => Project(CountryColumns, country);

        private static IReadOnlyDictionary<string, string?> Project<TEntity>(
            IReadOnlyList<(VerificationColumnDefinition Column, Func<TEntity, string?> Select)> columns, TEntity entity)
        {
            var values = new Dictionary<string, string?>(StringComparer.Ordinal);
            foreach (var (column, select) in columns)
                values[column.Name] = select(entity);
            return values;
        }

        private static string? FormatNumber(double? value) => value?.ToString(CultureInfo.InvariantCulture);
        private static string? FormatNumber(int? value) => value?.ToString(CultureInfo.InvariantCulture);
    }
}
