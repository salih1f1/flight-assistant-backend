using System.Globalization;
using FlightAssistant.Application.Interfaces.Repositories;
using FlightAssistant.Application.Interfaces.Verification;
using FlightAssistant.Application.Verification;
using FlightAssistant.Domain.Common;
using FlightAssistant.Domain.Common.Interfaces;
using FlightAssistant.Domain.Flights.Events;
using FlightAssistant.Domain.ReferenceData.Entities;
using FlightAssistant.Domain.Verification.Enums;
using MediatR;
using Microsoft.Extensions.Logging;

namespace FlightAssistant.Application.EventHandlers
{
    /// <summary>
    /// Queues automated verification checks for the airline and both airports a tracked flight uses,
    /// and their countries, whenever <see cref="FlightTracked"/> is published (#410). The event carries
    /// only ids (R2); this handler owns every decision about what to check and how.
    ///
    /// <para>
    /// <b>Reads (R8).</b> One row each for the airline and the two airports, never inside a loop, then
    /// one batched read for their distinct <see cref="Airport.CountryCode"/>/
    /// <see cref="Airline.CountryCode"/> values (R6) via <see cref="ICountryRepository.FindByCodesAsync"/>
    /// -- four reads at most per track request, not one round trip per country. None of this scales
    /// with anything but the flight's own fixed shape, unlike <c>FlightDeletedEventHandler</c>'s
    /// per-traveler work, which is why it stays a handful of straight-line awaits rather than a loop
    /// issuing one repository call per row.
    /// </para>
    ///
    /// <para>
    /// <b>Queueing (R7).</b> One <see cref="IVerificationQueue.EnqueueAsync"/> call per entity type with
    /// its keys batched -- at most three calls, and a type with nothing to queue gets none. The
    /// open-request skip, and the save, belong to the queue (its own documented behaviour); this
    /// handler only decides which keys go in and passes them through.
    /// </para>
    ///
    /// <para>
    /// <b>Never throws (R5).</b> One catch around the whole body, logged with the flight id. The
    /// dispatch is post-<b>commit</b>, not post-response: <c>DomainEventDispatchInterceptor</c>
    /// publishes from inside the triggering <c>SaveChangesAsync</c> call, which
    /// <c>TrackFlightCommandHandler</c> is still awaiting -- the endpoint has not yet built its
    /// response. An uncaught exception here would therefore unwind out of that
    /// <c>SaveChangesAsync</c> and become a 500 for a tracking write that already committed and
    /// cannot be rolled back, so a failure here must never surface to the traveler, and must never
    /// try to roll anything back (there is nothing left to roll back).
    /// </para>
    /// </summary>
    public sealed class FlightTrackedVerificationEventHandler : INotificationHandler<FlightTracked>
    {
        private readonly IAirlineRepository _airlines;
        private readonly IAirportRepository _airports;
        private readonly ICountryRepository _countries;
        private readonly IVerificationQueue _queue;
        private readonly IVerificationSourceRegistry _registry;
        private readonly IVerificationJobOptions _options;
        private readonly TimeProvider _timeProvider;
        private readonly ILogger<FlightTrackedVerificationEventHandler> _logger;

        public FlightTrackedVerificationEventHandler(
            IAirlineRepository airlines,
            IAirportRepository airports,
            ICountryRepository countries,
            IVerificationQueue queue,
            IVerificationSourceRegistry registry,
            IVerificationJobOptions options,
            TimeProvider timeProvider,
            ILogger<FlightTrackedVerificationEventHandler> logger)
        {
            _airlines = airlines;
            _airports = airports;
            _countries = countries;
            _queue = queue;
            _registry = registry;
            _options = options;
            _timeProvider = timeProvider;
            _logger = logger;
        }

        public async Task Handle(FlightTracked notification, CancellationToken ct)
        {
            try
            {
                var nowUtc = _timeProvider.GetUtcNow().UtcDateTime;
                var staleAfterDays = _options.StaleAfterDays;

                // R8: the airline and both airports, one read apiece, never inside a loop.
                var airline = await _airlines.GetByIdAsync(notification.AirlineId, ct);
                var departureAirport = await _airports.GetByIdAsync(notification.DepartureAirportId, ct);
                var arrivalAirport = await _airports.GetByIdAsync(notification.ArrivalAirportId, ct);

                if (airline is not null && NeedsVerification(airline, nowUtc, staleAfterDays))
                    await EnqueueAsync(VerificationEntityType.Airline, new[] { Key(airline.Id) }, ct);

                var airportKeys = AirportKeys(departureAirport, arrivalAirport, nowUtc, staleAfterDays);
                if (airportKeys.Count > 0)
                    await EnqueueAsync(VerificationEntityType.Airport, airportKeys, ct);

                var countryKeys = await CountryKeysAsync(
                    airline, departureAirport, arrivalAirport, nowUtc, staleAfterDays, ct);
                if (countryKeys.Count > 0)
                    await EnqueueAsync(VerificationEntityType.Country, countryKeys, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex,
                    "Failed to queue verification checks triggered by tracking flight {FlightId}",
                    notification.FlightId);
            }
        }

        private static List<string> AirportKeys(
            Airport? departure, Airport? arrival, DateTime nowUtc, int staleAfterDays)
        {
            var keys = new List<string>(2);
            if (departure is not null && NeedsVerification(departure, nowUtc, staleAfterDays))
                keys.Add(Key(departure.Id));
            if (arrival is not null && NeedsVerification(arrival, nowUtc, staleAfterDays))
                keys.Add(Key(arrival.Id));
            return keys;
        }

        private async Task<List<string>> CountryKeysAsync(
            Airline? airline, Airport? departure, Airport? arrival,
            DateTime nowUtc, int staleAfterDays, CancellationToken ct)
        {
            // R6: countries come from the rows themselves, de-duplicated before resolving -- the same
            // country behind two (or three) of the rows costs one read and produces one key, not two.
            var codes = new HashSet<string>(StringComparer.Ordinal);
            if (airline is not null) codes.Add(airline.CountryCode);
            if (departure is not null) codes.Add(departure.CountryCode);
            if (arrival is not null) codes.Add(arrival.CountryCode);

            // R8: one batched read for every distinct code, not one round trip per code -- a code
            // with no matching row is simply absent from the result, same as a null FindByCodeAsync
            // reply would have been.
            var countries = await _countries.FindByCodesAsync(codes, ct);

            var keys = new List<string>(countries.Count);
            foreach (var country in countries)
            {
                if (NeedsVerification(country, nowUtc, staleAfterDays))
                    keys.Add(country.Code);
            }
            return keys;
        }

        private Task EnqueueAsync(VerificationEntityType entityType, IReadOnlyCollection<string> keys, CancellationToken ct)
        {
            var sources = _options.DefaultSourcesFor(entityType);

            var cost = 0m;
            foreach (var name in sources)
                cost += _registry.TryGet(name)?.CostPerRowUsd ?? 0m;

            return _queue.EnqueueAsync(
                entityType, keys, sources, VerificationTrigger.Flight, VerificationJobActor.ActorId, cost, ct);
        }

        private static string Key(int id) => id.ToString(CultureInfo.InvariantCulture);

        /// <summary>
        /// Whether <paramref name="row"/> needs an automated check. R4 beats R3: a row an admin is
        /// already reviewing -- a non-blank <see cref="IVerifiable.VerificationIssue"/> -- is skipped
        /// outright, stale or not, because re-queueing it would overwrite that in-progress state the
        /// moment the check completed. Absent an issue, R3 treats the row as stale (and queues it) when
        /// any of five things is true: it is unverified; it has no verification timestamp; that
        /// timestamp is older than <paramref name="staleAfterDays"/>; that timestamp is in the
        /// <b>future</b> (an imported row keeps the file's own timestamp rather than the moment of
        /// import, and must not therefore read as fresh forever); or it was verified only by
        /// <see cref="VerificationSources.Import"/> -- weaker evidence than an automated check, since a
        /// hand-built CSV is stamped the same way even when recent.
        /// </summary>
        private static bool NeedsVerification(IVerifiable row, DateTime nowUtc, int staleAfterDays)
        {
            if (!string.IsNullOrEmpty(row.VerificationIssue))
                return false;

            return !row.IsVerified
                || row.VerifiedAt is null
                || row.VerifiedAt < nowUtc.AddDays(-staleAfterDays)
                || row.VerifiedAt > nowUtc
                || row.VerificationSource == VerificationSources.Import;
        }
    }
}
