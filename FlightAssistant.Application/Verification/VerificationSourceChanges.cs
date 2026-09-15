using FlightAssistant.Application.Commands.Admin.Airlines;
using FlightAssistant.Application.Commands.Admin.Airports;
using FlightAssistant.Domain.Common.Exceptions;
using FlightAssistant.Domain.ReferenceData.Entities;
using FlightAssistant.Domain.Verification.Enums;
using FluentValidation;

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
    /// <b>No second set of write rules.</b> The merged airport or airline row is checked with the admin
    /// edit's own validator (<see cref="UpdateAirportCommandValidator"/>, <see cref="UpdateAirlineCommandValidator"/>),
    /// so a source value the edit form would refuse (an IATA code that isn't three letters, a name over
    /// 200 characters, a five-character ICAO that would overflow its column) is refused here too, never
    /// by a copy of those rules. Like the edit, the check covers the whole row.
    /// </para>
    ///
    /// <para>
    /// <b>Failures are 409s.</b> A value that cannot be parsed, would leave a required column empty, or
    /// breaks the edit rules is a conflict between the stored check and our schema: the request is
    /// valid, the data it carries cannot be applied. Nothing is written to the entity until every
    /// check has passed.
    /// </para>
    /// </summary>
    public sealed class VerificationSourceChanges
    {
        private static readonly UpdateAirportCommandValidator AirportEditRules = new();
        private static readonly UpdateAirlineCommandValidator AirlineEditRules = new();

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
        /// than refused (controller ruling, #378). The check compared elevation as a number within
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
        /// <exception cref="DomainException">409: <c>Iata</c>, <c>Name</c>, <c>City</c> or <c>CountryCode</c>
        /// would become empty, or the merged row breaks <see cref="UpdateAirportCommandValidator"/>.</exception>
        public void ApplyTo(Airport airport)
        {
            ArgumentNullException.ThrowIfNull(airport);
            EnsureEntityType(VerificationEntityType.Airport);

            var merged = new UpdateAirportCommand(
                Id: airport.Id,
                Iata: RequiredString(nameof(Airport.Iata), airport.Iata),
                Icao: OptionalString(nameof(Airport.Icao), airport.Icao),
                Name: RequiredString(nameof(Airport.Name), airport.Name),
                City: RequiredString(nameof(Airport.City), airport.City),
                CountryCode: RequiredString(nameof(Airport.CountryCode), airport.CountryCode),
                Latitude: OptionalNumber(nameof(Airport.Latitude), airport.Latitude),
                Longitude: OptionalNumber(nameof(Airport.Longitude), airport.Longitude),
                ElevationFt: OptionalNumber(nameof(Airport.ElevationFt), airport.ElevationFt));
            EnsureEveryColumnApplied();
            EnsureEditRulesPass(AirportEditRules, merged);

            airport.Update(merged.Iata, merged.Icao, merged.Name, merged.City, merged.CountryCode,
                merged.Latitude, merged.Longitude, merged.ElevationFt);
        }

        /// <summary>Writes these changes to <paramref name="airline"/>, keeping its <c>AllianceCode</c>
        /// (not a checked column); nothing is written if any is refused.</summary>
        /// <exception cref="DomainException">409: <c>Name</c> or <c>CountryCode</c> would become empty,
        /// or the merged row breaks <see cref="UpdateAirlineCommandValidator"/>.</exception>
        public void ApplyTo(Airline airline)
        {
            ArgumentNullException.ThrowIfNull(airline);
            EnsureEntityType(VerificationEntityType.Airline);

            var merged = new UpdateAirlineCommand(
                Id: airline.Id,
                Iata: OptionalString(nameof(Airline.Iata), airline.Iata),
                Icao: OptionalString(nameof(Airline.Icao), airline.Icao),
                Name: RequiredString(nameof(Airline.Name), airline.Name),
                CountryCode: RequiredString(nameof(Airline.CountryCode), airline.CountryCode),
                AllianceCode: airline.AllianceCode);
            EnsureEveryColumnApplied();
            EnsureEditRulesPass(AirlineEditRules, merged);

            airline.Update(merged.Iata, merged.Icao, merged.Name, merged.CountryCode, merged.AllianceCode);
        }

        /// <summary>Writes these changes to <paramref name="country"/>, keeping <c>IsEU</c>,
        /// <c>IsSchengen</c> and <c>IsEEA</c> (not checked columns); nothing is written if any is refused.</summary>
        /// <exception cref="DomainException">409: <c>Name</c> would become empty, or the change includes
        /// <c>Code</c>, the country's key, which <c>Update</c> cannot change.</exception>
        public void ApplyTo(Country country)
        {
            ArgumentNullException.ThrowIfNull(country);
            EnsureEntityType(VerificationEntityType.Country);

            // No edit rules to reuse: there is no admin country update command, so no validator. The
            // format and length of Name, Alpha3, Region and Subregion are not checked here, and a
            // value over its column size still fails on save (review Minor 1 of #378 task 2, deferred).
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

        private void EnsureEditRulesPass<TUpdate>(IValidator<TUpdate> editRules, TUpdate merged)
        {
            var result = editRules.Validate(merged);
            if (result.IsValid) return;

            var columns = string.Join(", ", result.Errors.Select(e => e.PropertyName).Distinct(StringComparer.Ordinal));
            throw new DomainException(
                $"Accepting the source would leave the {_entityType} with an invalid {columns}.", 409,
                diagnosticDetail: string.Join(" ", result.Errors.Select(e => e.ErrorMessage)));
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
