using FlightAssistant.Domain.Verification.Enums;

namespace FlightAssistant.Application.Verification
{
    /// <summary>
    /// The verification job's settings that Application code reads (#367, spec §4.6). The API-layer
    /// <c>VerificationOptions</c> implements this and is bound and validated on start there;
    /// Application can't reference that class, so it reads it through this interface (the
    /// <c>IFlightSearchHorizonOptions</c> translation pattern).
    /// </summary>
    public interface IVerificationJobOptions
    {
        /// <summary>How many requests one cycle claims at most.</summary>
        int BatchSize { get; }

        /// <summary>How many source-error attempts a request gets before it completes as
        /// <c>Error</c>.</summary>
        int MaxAttempts { get; }

        /// <summary>How long a source's downloaded dataset is kept after its last lookup once no open
        /// request uses the source (#378). <see cref="TimeSpan.Zero"/> releases it as soon as none
        /// does. Never extends the 24-hour maximum age, and a cached failure keeps only its cooldown.</summary>
        TimeSpan IdleRelease { get; }

        /// <summary>How old a verification may be before a tracked flight re-queues the entity
        /// (#410). Not read by the job itself, which uses each request's own sources.</summary>
        int StaleAfterDays { get; }

        /// <summary>How long an entity whose most recent completed check was <c>Error</c> is left
        /// alone before the flight-tracking trigger re-queues it (#418) — read by
        /// <c>VerificationRequestRepository.EnqueueAsync</c> for the flight trigger only.
        /// <see cref="TimeSpan.Zero"/> disables the cooldown. Not read by the job itself, which
        /// claims whatever is <c>Pending</c> regardless of any prior <c>Error</c> outcome.</summary>
        TimeSpan ErrorCooldown { get; }

        /// <summary>How long a request may wait before it is claimed ahead of every younger request,
        /// whatever its trigger (#419) — read by <c>VerificationRequestRepository.ClaimBatchAsync</c>.
        /// A request is promoted once its <c>requested_utc</c> is at or before <c>nowUtc -
        /// ClaimPromotionAge</c> (inclusive). Promoted requests of any trigger rank with
        /// flight-triggered requests, ordered by <c>requested_utc</c> then <c>id</c>; an unpromoted
        /// admin request still ranks below every flight-triggered request, exactly as before this
        /// option existed. Never <see cref="TimeSpan.Zero"/> — that would degenerate into strict
        /// oldest-first claiming, which is not what was chosen.</summary>
        TimeSpan ClaimPromotionAge { get; }

        /// <summary>The source names a check for <paramref name="entityType"/> uses when a caller
        /// names none — read by the flight-tracking trigger (#410) to build its own
        /// <c>EnqueueAsync</c> call. Not read by the job itself, which uses each request's own
        /// sources.</summary>
        IReadOnlyList<string> DefaultSourcesFor(VerificationEntityType entityType);
    }
}
