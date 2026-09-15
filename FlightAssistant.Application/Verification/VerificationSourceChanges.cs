using FlightAssistant.Domain.Common.Exceptions;
using FlightAssistant.Domain.ReferenceData.Entities;
using FlightAssistant.Domain.Verification.Enums;

namespace FlightAssistant.Application.Verification
{
    /// <summary>
    /// Accept-source's write (#378, spec §4.4): one source's stored values for some checked columns,
    /// parsed into the entity's own types and applied through the entity's <c>Update</c>, with every
    /// other field passed through as it is.
    ///
    /// <para>
    /// <b>No second column list.</b> A value is parsed by its column's
    /// <see cref="VerificationColumnKind"/> from <see cref="VerificationCheckedColumns"/>, with
    /// <see cref="VerificationValueNormalizer"/>'s own parsing, so which kind a column is never
    /// appears here. Each <c>ApplyTo</c> overload names each <c>Update</c> parameter once, which it
    /// has to, and a column the check compares but <c>Update</c> has no parameter for
    /// (<c>Country.Code</c>, the lookup key) is refused with a 409 rather than silently not written.
    /// </para>
    ///
    /// <para>
    /// <b>Failures are 409s.</b> A value that cannot be parsed, or that would leave a required column
    /// empty, is a conflict between the stored check and our schema: the request is valid, the data
    /// it carries cannot be applied.
    /// </para>
    /// </summary>
    public sealed class VerificationSourceChanges
    {
        private readonly VerificationEntityType _entityType;
        private readonly IReadOnlyDictionary<string, object?> _values;
        private readonly HashSet<string> _applied = new(StringComparer.Ordinal);

        private VerificationSourceChanges(VerificationEntityType entityType, IReadOnlyDictionary<string, object?> values)
        {
            _entityType = entityType;
            _values = values;
        }

        /// <summary>
        /// Parses <paramref name="sourceValuesByColumn"/>, keyed by checked-column name, into the
        /// entity's types:
        /// <list type="bullet">
        /// <item>empty (null, blank, whitespace, as the comparer reads it): no value;</item>
        /// <item><see cref="VerificationColumnKind.Code"/>: trimmed and upper-cased, the form <c>Update</c> stores;</item>
        /// <item><see cref="VerificationColumnKind.Text"/>: trimmed;</item>
        /// <item><see cref="VerificationColumnKind.Coordinate"/>: a finite invariant-culture <see cref="double"/>;</item>
        /// <item><see cref="VerificationColumnKind.Elevation"/>: an <see cref="int"/> of feet. OurAirports
        /// publishes whole feet; a decimal is rounded to the nearest foot, half away from zero, rather
        /// than refused. The check compared elevation as a number within
        /// <see cref="VerificationComparer.ElevationToleranceFt"/>, so the rounded value still matches
        /// the source by the check's own rule;</item>
        /// <item><see cref="VerificationColumnKind.NumericCode"/>: an <see cref="int"/>, leading zeros allowed.</item>
        /// </list>
        /// </summary>
        /// <exception cref="DomainException">409: a value cannot be parsed for its column's kind.</exception>
        /// <exception cref="ArgumentException">A key is not a checked column of <paramref name="entityType"/>.</exception>
        public static VerificationSourceChanges Parse(
            VerificationEntityType entityType, IReadOnlyDictionary<string, string?> sourceValuesByColumn)
        {
            ArgumentNullException.ThrowIfNull(sourceValuesByColumn);
            var columns = VerificationCheckedColumns.GetColumns(entityType).ToDictionary(c => c.Name, StringComparer.Ordinal);

            var values = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var (name, raw) in sourceValuesByColumn)
            {
                if (!columns.TryGetValue(name, out var column))
                    throw new ArgumentException($"'{name}' is not a checked {entityType} column.", nameof(sourceValuesByColumn));
                values[name] = ParseValue(column, raw);
            }

            return new VerificationSourceChanges(entityType, values);
        }

        /// <summary>The parsed value for <paramref name="column"/>, when this change writes that column.
        /// Null means the column is cleared.</summary>
        public bool TryGetValue(string column, out object? value) => _values.TryGetValue(column, out value);

        /// <summary>Writes these changes to <paramref name="airport"/>; nothing is written if any is refused.</summary>
        /// <exception cref="DomainException">409: <c>Iata</c>, <c>Name</c>, <c>City</c> or <c>CountryCode</c> would become empty.</exception>
        public void ApplyTo(Airport airport)
        {
            ArgumentNullException.ThrowIfNull(airport);
            EnsureEntityType(VerificationEntityType.Airport);

            var iata = RequiredString(nameof(Airport.Iata), airport.Iata);
            var icao = OptionalString(nameof(Airport.Icao), airport.Icao);
            var name = RequiredString(nameof(Airport.Name), airport.Name);
            var city = RequiredString(nameof(Airport.City), airport.City);
            var countryCode = RequiredString(nameof(Airport.CountryCode), airport.CountryCode);
            var latitude = OptionalNumber(nameof(Airport.Latitude), airport.Latitude);
            var longitude = OptionalNumber(nameof(Airport.Longitude), airport.Longitude);
            var elevationFt = OptionalNumber(nameof(Airport.ElevationFt), airport.ElevationFt);
            EnsureEveryColumnApplied();

            airport.Update(iata, icao, name, city, countryCode, latitude, longitude, elevationFt);
        }

        /// <summary>Writes these changes to <paramref name="airline"/>, keeping its <c>AllianceCode</c>
        /// (not a checked column); nothing is written if any is refused.</summary>
        /// <exception cref="DomainException">409: <c>Name</c> or <c>CountryCode</c> would become empty.</exception>
        public void ApplyTo(Airline airline)
        {
            ArgumentNullException.ThrowIfNull(airline);
            EnsureEntityType(VerificationEntityType.Airline);

            var iata = OptionalString(nameof(Airline.Iata), airline.Iata);
            var icao = OptionalString(nameof(Airline.Icao), airline.Icao);
            var name = RequiredString(nameof(Airline.Name), airline.Name);
            var countryCode = RequiredString(nameof(Airline.CountryCode), airline.CountryCode);
            EnsureEveryColumnApplied();

            airline.Update(iata, icao, name, countryCode, airline.AllianceCode);
        }

        /// <summary>Writes these changes to <paramref name="country"/>, keeping <c>IsEU</c>,
        /// <c>IsSchengen</c> and <c>IsEEA</c> (not checked columns); nothing is written if any is refused.</summary>
        /// <exception cref="DomainException">409: <c>Name</c> would become empty, or the change includes
        /// <c>Code</c>, the country's key, which <c>Update</c> cannot change.</exception>
        public void ApplyTo(Country country)
        {
            ArgumentNullException.ThrowIfNull(country);
            EnsureEntityType(VerificationEntityType.Country);

            var name = RequiredString(nameof(Country.Name), country.Name);
            var alpha3 = OptionalString(nameof(Country.Alpha3), country.Alpha3);
            var numericCode = OptionalNumber(nameof(Country.NumericCode), country.NumericCode);
            var region = OptionalString(nameof(Country.Region), country.Region);
            var subregion = OptionalString(nameof(Country.Subregion), country.Subregion);
            EnsureEveryColumnApplied();

            country.Update(name, alpha3, numericCode, region, subregion, country.IsEU, country.IsSchengen, country.IsEEA);
        }

        private static object? ParseValue(VerificationColumnDefinition column, string? raw)
        {
            if (VerificationValueNormalizer.IsEmpty(raw)) return null;
            var value = raw!;

            switch (column.Kind)
            {
                case VerificationColumnKind.Code:
                    return VerificationValueNormalizer.NormalizeCode(value);

                case VerificationColumnKind.Text:
                    return value.Trim();

                case VerificationColumnKind.Coordinate:
                    return VerificationValueNormalizer.TryParseNumber(value, out var coordinate) && double.IsFinite(coordinate)
                        ? coordinate
                        : throw Unparseable(column, value);

                case VerificationColumnKind.Elevation:
                    if (!VerificationValueNormalizer.TryParseNumber(value, out var elevation) || !double.IsFinite(elevation))
                        throw Unparseable(column, value);
                    var feet = Math.Round(elevation, MidpointRounding.AwayFromZero);
                    return feet is >= int.MinValue and <= int.MaxValue ? (int)feet : throw Unparseable(column, value);

                case VerificationColumnKind.NumericCode:
                    return VerificationValueNormalizer.TryParseNumericCode(value, out var numericCode)
                        ? numericCode
                        : throw Unparseable(column, value);

                default:
                    throw new ArgumentOutOfRangeException(nameof(column), column.Kind, "Unknown column kind.");
            }
        }

        private string RequiredString(string column, string current)
        {
            _applied.Add(column);
            if (!_values.TryGetValue(column, out var value)) return current;

            return value as string
                ?? throw new DomainException(
                    $"Accepting the source would leave the required {_entityType} {column} empty.", 409);
        }

        private string? OptionalString(string column, string? current)
        {
            _applied.Add(column);
            return _values.TryGetValue(column, out var value) ? (string?)value : current;
        }

        private T? OptionalNumber<T>(string column, T? current) where T : struct
        {
            _applied.Add(column);
            return _values.TryGetValue(column, out var value) ? (T?)value : current;
        }

        private void EnsureEveryColumnApplied()
        {
            var notApplied = _values.Keys.FirstOrDefault(column => !_applied.Contains(column));
            if (notApplied is not null)
                throw new DomainException(
                    $"{_entityType} {notApplied} cannot be changed by accepting a source.", 409);
        }

        private void EnsureEntityType(VerificationEntityType target)
        {
            if (_entityType != target)
                throw new InvalidOperationException($"These are {_entityType} changes; they cannot be applied to a {target}.");
        }

        private static DomainException Unparseable(VerificationColumnDefinition column, string value) =>
            new($"The source's {column.Name} value could not be parsed.", 409,
                diagnosticDetail: $"Stored {column.Kind} value: '{value}'.");
    }
}
