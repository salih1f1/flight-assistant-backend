using System.Globalization;
using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Application.Interfaces.Verification;
using FlightAssistant.Domain.Common.Interfaces;
using FlightAssistant.Domain.Verification.Entities;
using FlightAssistant.Domain.Verification.Enums;
using Microsoft.Extensions.Logging;

namespace FlightAssistant.Application.Verification
{
    /// <summary>
    /// A request the cycle claimed: its id and the <c>StartedUtc</c> the claim stamped on it. The
    /// processor re-loads the request by id in its own scope, and acts only while the row still
    /// carries this <see cref="StartedUtc"/>.
    /// </summary>
    public sealed record VerificationClaim(long RequestId, DateTime StartedUtc)
    {
        /// <summary>The claim for a request <c>ClaimBatchAsync</c> just returned.</summary>
        public static VerificationClaim From(VerificationRequest claimed)
        {
            ArgumentNullException.ThrowIfNull(claimed);
            if (claimed.Status != VerificationRequestStatus.Running || claimed.StartedUtc is not { } startedUtc)
                throw new InvalidOperationException(
                    $"Verification request {claimed.Id} is {claimed.Status}, not a claimed Running request.");

            return new VerificationClaim(claimed.Id, startedUtc);
        }
    }

    /// <summary>What processing one claimed request did to it.</summary>
    public enum VerificationProcessOutcome
    {
        /// <summary>Every source matched: the entity is verified.</summary>
        Verified,

        /// <summary>A source disagreed: the entity needs attention.</summary>
        Mismatch,

        /// <summary>A source had no row: the entity needs attention.</summary>
        NotFound,

        /// <summary>A source error below <c>MaxAttempts</c>: back to Pending with a backoff.</summary>
        RetryScheduled,

        /// <summary>Completed as Error: <c>MaxAttempts</c> reached, the entity is gone, or a source
        /// is unusable.</summary>
        Error,

        /// <summary>The claim was lost to another claim; nothing was written.</summary>
        ClaimLost,

        /// <summary>
        /// Processing threw, and recording that as a source error threw too, so nothing was saved: the
        /// request is still Running with no attempt counted. The stuck-request reset returns it to
        /// Pending after 10 minutes and counts the attempt then. Only the cycle runner reports this.
        /// </summary>
        FailureUnrecorded
    }

    /// <summary>
    /// Handles one claimed verification request end to end (#367, spec §4.2): re-load it, check its
    /// sources, load the entity, fetch every source, compare, and save the outcome to the entity and
    /// the request in one <c>SaveChanges</c>. Resolved from a DI scope of its own for each request, so
    /// a save that fails never leaves tracked changes behind for the next request.
    ///
    /// <para>
    /// <b>The order of checks.</b> Sources are checked before anything is fetched: a request naming a
    /// source that isn't registered (e.g. <c>aeroapi</c> before step 9), or one that doesn't support
    /// the entity type, completes as <c>Error</c> straight away and is never retried, since retrying
    /// cannot change the answer. Then the entity is loaded (<c>"Entity no longer exists"</c> when it
    /// was deleted), then the sources are fetched in request order. The first source error stops the
    /// fetch, so a request does not spend further lookups on an attempt that will be retried anyway.
    /// </para>
    ///
    /// <para>
    /// <b>Claim ownership.</b> Re-loading a request reads its current concurrency token, which alone
    /// would let this processor overwrite a claim another cycle took since. So a re-loaded request is
    /// processed only while it is still Running with the claim's <c>StartedUtc</c>; after that check,
    /// the token catches a claim lost before the save, surfacing as
    /// <see cref="VerificationClaimLostException"/>. Either way the outcome is
    /// <see cref="VerificationProcessOutcome.ClaimLost"/> and no attempt is recorded.
    /// </para>
    /// </summary>
    public sealed class VerificationRequestProcessor
    {
        public const string EntityNoLongerExistsError = "Entity no longer exists";

        /// <summary>Joins source names in <c>IVerifiable.VerificationSource</c>.</summary>
        public const char VerifiedSourceSeparator = '+';

        private readonly IVerificationRequestRepository _requests;
        private readonly IAirportRepository _airports;
        private readonly IAirlineRepository _airlines;
        private readonly ICountryRepository _countries;
        private readonly IVerificationSourceRegistry _registry;
        private readonly IVerificationJobOptions _options;
        private readonly TimeProvider _timeProvider;
        private readonly ILogger<VerificationRequestProcessor> _logger;

        public VerificationRequestProcessor(
            IVerificationRequestRepository requests,
            IAirportRepository airports,
            IAirlineRepository airlines,
            ICountryRepository countries,
            IVerificationSourceRegistry registry,
            IVerificationJobOptions options,
            TimeProvider timeProvider,
            ILogger<VerificationRequestProcessor> logger)
        {
            _requests = requests;
            _airports = airports;
            _airlines = airlines;
            _countries = countries;
            _registry = registry;
            _options = options;
            _timeProvider = timeProvider;
            _logger = logger;
        }

        /// <summary>Processes one claimed request and saves its outcome.</summary>
        /// <exception cref="OperationCanceledException"><paramref name="ct"/> was cancelled; nothing
        /// is saved and the request stays Running until the stuck-request reset.</exception>
        public async Task<VerificationProcessOutcome> ProcessAsync(VerificationClaim claim, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(claim);

            var request = await LoadOwnedRequestAsync(claim, ct);
            if (request is null) return VerificationProcessOutcome.ClaimLost;

            var sources = new List<IVerificationSource>();
            foreach (var name in request.SourceNames)
            {
                var source = _registry.TryGet(name);
                if (source is null)
                    return await CompleteAsErrorAsync(request, $"Source '{name}' is not registered.", ct);
                if (!source.SupportedEntityTypes.Contains(request.EntityType))
                    return await CompleteAsErrorAsync(request, $"Source '{name}' does not support {request.EntityType}.", ct);
                sources.Add(source);
            }

            var entity = await LoadEntityAsync(request, ct);
            if (entity is null)
                return await CompleteAsErrorAsync(request, EntityNoLongerExistsError, ct);

            var results = new List<VerificationSourceResult>(sources.Count);
            foreach (var source in sources)
            {
                var fetched = await source.LookupAsync(request.EntityType, entity.LookupKey, ct);
                if (fetched.IsError)
                    return await RecordSourceErrorAsync(request, $"{source.Name}: {fetched.Error}", ct);

                results.Add(new VerificationSourceResult(source.Name, fetched.Lookup!));
            }

            var comparison = VerificationComparer.Compare(request.EntityType, entity.OurValues, results);
            var now = UtcNow();

            VerificationProcessOutcome outcome;
            switch (comparison.Outcome)
            {
                case VerificationComparisonOutcome.Verified:
                    entity.Entity.Verify(
                        VerificationJobActor.ActorId,
                        string.Join(VerifiedSourceSeparator, results.Select(r => r.SourceName)),
                        now);
                    request.MarkVerified(now);
                    outcome = VerificationProcessOutcome.Verified;
                    break;

                case VerificationComparisonOutcome.Mismatch:
                    entity.Entity.MarkVerificationFailed(comparison.Summary!);
                    request.MarkMismatch(
                        VerificationJson.SerializeDifferences(comparison.Differences),
                        VerificationJson.SerializeSourceValues(comparison.SourceValues),
                        now);
                    outcome = VerificationProcessOutcome.Mismatch;
                    break;

                case VerificationComparisonOutcome.NotFound:
                    entity.Entity.MarkVerificationFailed(comparison.Summary!);
                    request.MarkNotFound(VerificationJson.SerializeSourceValues(comparison.SourceValues), now);
                    outcome = VerificationProcessOutcome.NotFound;
                    break;

                default:
                    throw new InvalidOperationException($"Unknown comparison outcome {comparison.Outcome}.");
            }

            return await SaveAsync(request, outcome, ct);
        }

        /// <summary>
        /// Records an exception that escaped <see cref="ProcessAsync"/> as a source error on the
        /// request (attempts + 1, backoff, <c>Error</c> at <c>MaxAttempts</c>). Call it from a fresh
        /// scope: the one that threw may still track the failed changes.
        ///
        /// <para>
        /// Only the exception's type is stored (<see cref="UnexpectedErrorMessage"/>). An EF, Npgsql
        /// or argument exception's message can carry a host and port, table and constraint names or
        /// key values, and <c>verification_requests.error</c> is shown to admins in every environment,
        /// Production included. The caller logs the full exception with the request id and the
        /// cycle's correlation id.
        /// </para>
        /// </summary>
        public async Task<VerificationProcessOutcome> RecordUnexpectedFailureAsync(
            VerificationClaim claim, Exception exception, CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(claim);
            ArgumentNullException.ThrowIfNull(exception);

            var request = await LoadOwnedRequestAsync(claim, ct);
            if (request is null) return VerificationProcessOutcome.ClaimLost;

            return await RecordSourceErrorAsync(
                request, UnexpectedErrorMessage(exception), ct);
        }

        /// <summary>The stored error for an unexpected exception: its type, never its message.</summary>
        public static string UnexpectedErrorMessage(Exception exception)
        {
            ArgumentNullException.ThrowIfNull(exception);
            return $"Unexpected error ({exception.GetType().Name}); see logs";
        }

        private async Task<VerificationRequest?> LoadOwnedRequestAsync(VerificationClaim claim, CancellationToken ct)
        {
            var request = await _requests.GetByIdAsync(claim.RequestId, ct);
            if (request is { Status: VerificationRequestStatus.Running } && request.StartedUtc == claim.StartedUtc)
                return request;

            _logger.LogWarning(
                "Verification request {RequestId} is no longer this cycle's claim (now {Status}, started {StartedUtc}); " +
                "skipping it without recording an attempt.",
                claim.RequestId, request?.Status, request?.StartedUtc);
            return null;
        }

        private async Task<LoadedEntity?> LoadEntityAsync(VerificationRequest request, CancellationToken ct)
        {
            switch (request.EntityType)
            {
                case VerificationEntityType.Airport:
                {
                    var airport = await _airports.GetByIdAsync(ParseId(request), ct);
                    return airport is null
                        ? null
                        : new LoadedEntity(airport, VerificationSourceLookupKey.ForAirport(airport.Iata, airport.Icao),
                            VerificationCheckedColumns.Project(airport));
                }

                case VerificationEntityType.Airline:
                {
                    var airline = await _airlines.GetByIdAsync(ParseId(request), ct);
                    return airline is null
                        ? null
                        : new LoadedEntity(airline, VerificationSourceLookupKey.ForAirport(airline.Iata, airline.Icao),
                            VerificationCheckedColumns.Project(airline));
                }

                case VerificationEntityType.Country:
                {
                    var country = await _countries.FindByCodeAsync(request.EntityKey, ct);
                    return country is null
                        ? null
                        : new LoadedEntity(country, VerificationSourceLookupKey.ForCountry(country.Code),
                            VerificationCheckedColumns.Project(country));
                }

                default:
                    throw new InvalidOperationException($"Unknown entity type {request.EntityType}.");
            }
        }

        // VerificationRequest.Create stores airport and airline keys as canonical positive ints.
        private static int ParseId(VerificationRequest request) =>
            int.Parse(request.EntityKey, NumberStyles.None, CultureInfo.InvariantCulture);

        private Task<VerificationProcessOutcome> CompleteAsErrorAsync(
            VerificationRequest request, string error, CancellationToken ct)
        {
            _logger.LogWarning(
                "Verification request {RequestId} ({EntityType} {EntityKey}) failed without retry: {Error}",
                request.Id, request.EntityType, request.EntityKey, error);

            request.MarkError(error, UtcNow());
            return SaveAsync(request, VerificationProcessOutcome.Error, ct);
        }

        private Task<VerificationProcessOutcome> RecordSourceErrorAsync(
            VerificationRequest request, string error, CancellationToken ct)
        {
            var backoff = VerificationRetryBackoff.After(request.Attempts + 1);
            request.RecordSourceError(error, UtcNow(), _options.MaxAttempts, backoff);

            var outcome = request.Status == VerificationRequestStatus.Error
                ? VerificationProcessOutcome.Error
                : VerificationProcessOutcome.RetryScheduled;

            _logger.LogWarning(
                "Verification request {RequestId} ({EntityType} {EntityKey}) hit a source error on attempt {Attempt} of {MaxAttempts}; {Result}: {Error}",
                request.Id, request.EntityType, request.EntityKey, request.Attempts, _options.MaxAttempts,
                outcome == VerificationProcessOutcome.Error ? "giving up" : $"retrying after {backoff}", error);

            return SaveAsync(request, outcome, ct);
        }

        private async Task<VerificationProcessOutcome> SaveAsync(
            VerificationRequest request, VerificationProcessOutcome outcome, CancellationToken ct)
        {
            try
            {
                await _requests.SaveChangesAsync(ct);
                return outcome;
            }
            catch (VerificationClaimLostException ex)
            {
                _logger.LogWarning(ex,
                    "Verification request {RequestId} was claimed again before its {Outcome} outcome was saved; discarding it.",
                    request.Id, outcome);
                return VerificationProcessOutcome.ClaimLost;
            }
        }

        private DateTime UtcNow() => _timeProvider.GetUtcNow().UtcDateTime;

        private sealed record LoadedEntity(
            IVerifiable Entity,
            VerificationSourceLookupKey LookupKey,
            IReadOnlyDictionary<string, string?> OurValues);
    }
}
