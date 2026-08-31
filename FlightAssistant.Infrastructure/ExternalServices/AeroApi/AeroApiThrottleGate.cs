using FlightAssistant.Application.Interfaces.ExternalServices;

namespace FlightAssistant.Infrastructure.ExternalServices.AeroApi
{
    /// <summary>
    /// Global outbound throttle for AeroAPI. Registered as a singleton and shared by every AeroAPI
    /// <see cref="HttpClient"/> (via <see cref="AeroApiThrottlingHandler"/>) so the personal tier's
    /// ~5 queries/minute limit is enforced across the whole process, not per client.
    ///
    /// Each caller reserves the next available send slot under a lock, then waits out its own
    /// delay without holding the lock — so callers queue for a turn but don't serialize on network
    /// I/O. <see cref="TimeProvider"/> is injected so tests can advance a fake clock instead of
    /// sleeping.
    /// </summary>
    public sealed class AeroApiThrottleGate : IAeroApiThrottleObserver
    {
        private readonly TimeProvider _timeProvider;
        private readonly SemaphoreSlim _reservationLock = new(1, 1);
        private DateTimeOffset? _nextAvailableSlotUtc;

        public AeroApiThrottleGate(TimeProvider timeProvider)
        {
            _timeProvider = timeProvider;
        }

        /// <summary>
        /// The instant the next outbound slot falls due, or null before any reservation has ever
        /// been made. A read-only observation for the usage dashboard (issue #300) — e.g. "next
        /// AeroAPI slot in 7s" — that explains a slow search at a glance. Read under
        /// <see cref="_reservationLock"/>, the same lock every reservation/release goes through, so
        /// this never sees a torn write (a <see cref="DateTimeOffset"/>? is multi-word and cannot
        /// be read atomically); it never itself reserves or releases a slot.
        /// </summary>
        public DateTimeOffset? NextSlotUtc
        {
            get
            {
                _reservationLock.Wait();
                try
                {
                    return _nextAvailableSlotUtc;
                }
                finally
                {
                    _reservationLock.Release();
                }
            }
        }

        /// <summary>
        /// Blocks until it is this caller's turn to send, given <paramref name="minInterval"/>
        /// since the previous reserved send.
        ///
        /// <para>
        /// A reservation that is abandoned — the caller's wait is cancelled by a timeout or a
        /// dropped request — is <b>released</b> rather than left standing. Without that, an
        /// attempt killed mid-wait would still have pushed the queue a whole interval further out,
        /// so each retry made the next caller strictly worse while nothing was ever sent.
        /// </para>
        /// </summary>
        public async Task WaitForTurnAsync(TimeSpan minInterval, CancellationToken ct)
        {
            var reservation = await ReserveSlotAsync(minInterval, ct);
            if (reservation.Delay <= TimeSpan.Zero) return;

            try
            {
                await Task.Delay(reservation.Delay, _timeProvider, ct);
            }
            catch (OperationCanceledException)
            {
                await ReleaseSlotAsync(reservation);
                throw;
            }
        }

        /// <summary>A claim on a send slot: how long the caller must wait for it, the instant it
        /// falls due, and the instant the gate was pushed to in order to reserve it.</summary>
        private readonly record struct Reservation(
            TimeSpan Delay, DateTimeOffset SlotUtc, DateTimeOffset NextSlotUtc);

        private async Task<Reservation> ReserveSlotAsync(TimeSpan minInterval, CancellationToken ct)
        {
            await _reservationLock.WaitAsync(ct);
            try
            {
                var now = _timeProvider.GetUtcNow();
                var earliestSlot = _nextAvailableSlotUtc is { } next && next > now ? next : now;

                _nextAvailableSlotUtc = earliestSlot + minInterval;

                return new Reservation(earliestSlot - now, earliestSlot, _nextAvailableSlotUtc.Value);
            }
            finally
            {
                _reservationLock.Release();
            }
        }

        private async Task ReleaseSlotAsync(Reservation reservation)
        {
            // CancellationToken.None deliberately: the caller's token is already cancelled — that is
            // why we are here — and failing to give the slot back is exactly the leak being fixed.
            await _reservationLock.WaitAsync(CancellationToken.None);
            try
            {
                // Only roll back if nobody has reserved behind us. If someone has, the gate has
                // already been spaced off our slot and rewinding it would let them send too early.
                // Rewinding to SlotUtc (not further) preserves the interval since the last send
                // that actually happened, while freeing the slot we never used.
                if (_nextAvailableSlotUtc == reservation.NextSlotUtc)
                    _nextAvailableSlotUtc = reservation.SlotUtc;
            }
            finally
            {
                _reservationLock.Release();
            }
        }
    }
}
