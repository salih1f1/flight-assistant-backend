using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Application.Verification;
using FlightAssistant.Domain.Common;
using FlightAssistant.Domain.Common.Exceptions;
using FlightAssistant.Domain.ReferenceData.Entities;
using FlightAssistant.Domain.Verification.Entities;
using FlightAssistant.Domain.Verification.Enums;
using FluentValidation;
using MediatR;

namespace FlightAssistant.Application.Commands.Admin.Verification
{
    /// <summary>
    /// An admin accepts one source's values for a <c>Mismatch</c> check (#378, spec §4.4): the
    /// source's stored values are written to the entity for the columns that source reported as
    /// different, and the entity is verified by the admin with that source.
    /// </summary>
    /// <param name="RequestId">A <c>verification_requests</c> id.</param>
    /// <param name="Source">One of the request's sources.</param>
    /// <param name="VerifiedBy">The admin actor, resolved by the endpoint; stored as <c>VerifiedBy</c>.</param>
    public sealed record AcceptVerificationSourceCommand(long RequestId, string Source, string VerifiedBy) : IRequest;

    public sealed class AcceptVerificationSourceCommandValidator : AbstractValidator<AcceptVerificationSourceCommand>
    {
        public AcceptVerificationSourceCommandValidator()
        {
            RuleFor(x => x.RequestId).GreaterThan(0);

            // Whether it is one of the request's sources needs the request: the handler's 400.
            RuleFor(x => x.Source).NotEmpty();

            RuleFor(x => x.VerifiedBy)
                .NotEmpty()
                .MaximumLength(VerificationGuard.MaxVerifiedByLength);
        }
    }

    /// <summary>
    /// <list type="number">
    /// <item>404 when the request doesn't exist; 409 unless it is <c>Mismatch</c>; 400 when the source
    /// isn't one of its sources or has no stored values.</item>
    /// <item>The source's differences only (user decision): columns it matched keep our own formatting,
    /// and columns only another source disagreed on are neither checked nor written.</item>
    /// <item>404 when the entity no longer exists.</item>
    /// <item>409 when a differing column's current value no longer matches the <c>ours</c> the check
    /// stored, by <see cref="VerificationComparer.ColumnsMatch"/>: the row changed since the check.</item>
    /// <item>409 when a new value for a unique column already belongs to another row.</item>
    /// <item>409 when a value can't be parsed, would empty a required column, or breaks the admin edit's
    /// rules (<see cref="VerificationSourceChanges"/>).</item>
    /// <item>The entity's <c>Update</c>, then <c>Verify(actor, source)</c>, then one save.</item>
    /// </list>
    /// The request itself is not changed: it stays <c>Mismatch</c> as history. Accepting the same source
    /// again fails the changed-row check whenever that source had differences, because those columns now
    /// hold its values. When it had none (only another source disagreed), a second accept succeeds and
    /// simply verifies the entity again: no data is written, only the verification stamp is renewed.
    /// </summary>
    public sealed class AcceptVerificationSourceCommandHandler : IRequestHandler<AcceptVerificationSourceCommand>
    {
        private readonly IVerificationRequestRepository _requests;
        private readonly IAirportRepository _airports;
        private readonly IAirlineRepository _airlines;
        private readonly ICountryRepository _countries;
        private readonly TimeProvider _timeProvider;

        public AcceptVerificationSourceCommandHandler(
            IVerificationRequestRepository requests,
            IAirportRepository airports,
            IAirlineRepository airlines,
            ICountryRepository countries,
            TimeProvider timeProvider)
        {
            _requests = requests;
            _airports = airports;
            _airlines = airlines;
            _countries = countries;
            _timeProvider = timeProvider;
        }

        public async Task Handle(AcceptVerificationSourceCommand command, CancellationToken ct)
        {
            var request = await _requests.GetByIdAsync(command.RequestId, ct)
                ?? throw new DomainException($"Verification request {command.RequestId} not found.", 404);

            if (request.Status != VerificationRequestStatus.Mismatch)
                throw new DomainException(
                    $"Verification request {command.RequestId} is {request.Status}; only a Mismatch can be accepted.", 409);

            if (!request.SourceNames.Contains(command.Source, StringComparer.Ordinal))
                throw new DomainException(
                    $"'{command.Source}' is not one of verification request {command.RequestId}'s sources.", 400);

            // MarkMismatch always stores both JSON columns.
            var storedValues = VerificationJson.DeserializeSourceValues(request.SourceValuesJson!);
            if (!storedValues.TryGetValue(command.Source, out var sourceValues))
                throw new DomainException(
                    $"Verification request {command.RequestId} has no stored values from '{command.Source}'.", 400);

            // Empty when this source matched and only another source disagreed: accepting then only verifies.
            var differences = VerificationJson.DeserializeDifferences(request.DifferencesJson!)
                .Where(d => string.Equals(d.SourceName, command.Source, StringComparison.Ordinal))
                .ToList();

            var accepted = new Accepted(request, command, differences, sourceValues);
            switch (request.EntityType)
            {
                case VerificationEntityType.Airport:
                    await AcceptForAirportAsync(accepted, ct);
                    break;
                case VerificationEntityType.Airline:
                    await AcceptForAirlineAsync(accepted, ct);
                    break;
                case VerificationEntityType.Country:
                    await AcceptForCountryAsync(accepted, ct);
                    break;
                default:
                    throw new InvalidOperationException($"Unknown entity type {request.EntityType}.");
            }
        }

        private async Task AcceptForAirportAsync(Accepted accepted, CancellationToken ct)
        {
            var airport = await _airports.GetByIdAsync(VerificationRequestProcessor.ParseId(accepted.Request), ct)
                ?? throw NoLongerExists(accepted.Request);

            var changes = ChangesSinceCheck(accepted, VerificationCheckedColumns.Project(airport));
            await EnsureUniqueValuesAreFreeAsync<Airport>(changes, airport, a => a.Id,
                (nameof(Airport.Iata), value => _airports.FindByIataAsync((string)value, ct)),
                (nameof(Airport.Icao), value => _airports.FindByIcaoAsync((string)value, ct)));
            changes.ApplyTo(airport);
            airport.Verify(accepted.Command.VerifiedBy, accepted.Command.Source, UtcNow());

            await _airports.SaveChangesAsync(ct);
        }

        private async Task AcceptForAirlineAsync(Accepted accepted, CancellationToken ct)
        {
            var airline = await _airlines.GetByIdAsync(VerificationRequestProcessor.ParseId(accepted.Request), ct)
                ?? throw NoLongerExists(accepted.Request);

            var changes = ChangesSinceCheck(accepted, VerificationCheckedColumns.Project(airline));
            await EnsureUniqueValuesAreFreeAsync<Airline>(changes, airline, a => a.Id,
                (nameof(Airline.Iata), value => _airlines.FindByIataAsync((string)value, ct)),
                (nameof(Airline.Icao), value => _airlines.FindByIcaoAsync((string)value, ct)));
            changes.ApplyTo(airline);
            airline.Verify(accepted.Command.VerifiedBy, accepted.Command.Source, UtcNow());

            await _airlines.SaveChangesAsync(ct);
        }

        private async Task AcceptForCountryAsync(Accepted accepted, CancellationToken ct)
        {
            var country = await _countries.FindByCodeAsync(accepted.Request.EntityKey, ct)
                ?? throw NoLongerExists(accepted.Request);

            var changes = ChangesSinceCheck(accepted, VerificationCheckedColumns.Project(country));
            await EnsureUniqueValuesAreFreeAsync<Country>(changes, country, c => c.Code,
                (nameof(Country.Alpha3), value => _countries.FindByAlpha3Async((string)value, ct)),
                (nameof(Country.NumericCode), value => _countries.FindByNumericCodeAsync((int)value, ct)));
            changes.ApplyTo(country);
            country.Verify(accepted.Command.VerifiedBy, accepted.Command.Source, UtcNow());

            await _countries.SaveChangesAsync(ct);
        }

        /// <summary>
        /// The source's values for its differing columns, once each column is confirmed unchanged since
        /// the check. Unchanged means <see cref="VerificationComparer.ColumnsMatch"/> by the column's
        /// kind, the rule the check itself used, not raw string equality: <c>İstanbul Airport</c> is
        /// still <c>Istanbul Airport</c>, and a latitude moved by less than the tolerance has not moved.
        /// </summary>
        private static VerificationSourceChanges ChangesSinceCheck(
            Accepted accepted, IReadOnlyDictionary<string, string?> ourCurrentValues)
        {
            var request = accepted.Request;
            var columns = VerificationCheckedColumns.GetColumns(request.EntityType);
            var toWrite = new Dictionary<string, string?>(StringComparer.Ordinal);

            foreach (var difference in accepted.Differences)
            {
                var column = columns.FirstOrDefault(c => string.Equals(c.Name, difference.Column, StringComparison.Ordinal))
                    ?? throw new DomainException(
                        $"{request.EntityType} {difference.Column} is no longer a checked column; queue a new check.", 409);

                ourCurrentValues.TryGetValue(column.Name, out var current);
                if (!VerificationComparer.ColumnsMatch(column.Kind, current, difference.Ours))
                    throw new DomainException(
                        $"{request.EntityType} {request.EntityKey}'s {column.Name} has changed since the check; queue a new check.", 409);

                // A column missing from the stored record reads as empty, as it did to the comparer.
                accepted.SourceValues.TryGetValue(column.Name, out var sourceValue);
                toWrite[column.Name] = sourceValue;
            }

            return VerificationSourceChanges.Parse(request.EntityType, toWrite);
        }

        /// <summary>
        /// Refuses, with a 409 naming the column, a new value for a unique column that another row already
        /// has: <c>airports.Iata</c>/<c>Icao</c>, <c>airlines.Iata</c>/<c>Icao</c> and
        /// <c>countries.Alpha3</c>/<c>NumericCode</c> all carry unique indexes, and nothing translates their
        /// violation on save, which would be a 500. Only the columns this accept writes are looked up, and
        /// a cleared column needs no lookup: those indexes allow any number of nulls. Each value is already
        /// in the form the entity will store (<see cref="VerificationSourceChanges.Parse"/> trims and
        /// upper-cases codes), so the lookup matches what the index will compare.
        ///
        /// <para>
        /// This is check-then-save, so it has a race: another write can take the value between this query
        /// and <c>SaveChangesAsync</c>, and that collision still surfaces as the database's unique violation
        /// (a 500). Accepted deliberately (controller ruling, #378): translating the violation in
        /// Infrastructure would change every reference-data write, and two admins racing for one code is rare.
        /// </para>
        /// </summary>
        /// <param name="keyOf">The entity's key, to tell another row from this one.</param>
        /// <param name="uniqueColumns">Each unique checked column, with the lookup that finds the row owning a value.</param>
        private static async Task EnsureUniqueValuesAreFreeAsync<TEntity>(
            VerificationSourceChanges changes,
            TEntity entity,
            Func<TEntity, object> keyOf,
            params (string Column, Func<object, Task<TEntity?>> FindOwner)[] uniqueColumns)
            where TEntity : class
        {
            foreach (var (column, findOwner) in uniqueColumns)
            {
                if (!changes.TryGetValue(column, out var value) || value is null)
                    continue;

                var owner = await findOwner(value);
                if (owner is not null && !Equals(keyOf(owner), keyOf(entity)))
                    throw new DomainException(
                        $"{typeof(TEntity).Name} {column} {value} already belongs to {typeof(TEntity).Name} {keyOf(owner)}.", 409);
            }
        }

        private static DomainException NoLongerExists(VerificationRequest request) =>
            new($"{request.EntityType} {request.EntityKey} no longer exists.", 404);

        private DateTime UtcNow() => _timeProvider.GetUtcNow().UtcDateTime;

        private sealed record Accepted(
            VerificationRequest Request,
            AcceptVerificationSourceCommand Command,
            IReadOnlyList<VerificationDifference> Differences,
            IReadOnlyDictionary<string, string?> SourceValues);
    }
}
