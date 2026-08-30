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
    public sealed class AeroApiThrottleGate
    {
        private readonly TimeProvider _timeProvider;
        private readonly SemaphoreSlim _reservationLock = new(1, 1);
        private DateTimeOffset? _nextAvailableSlotUtc;

        public AeroApiThrottleGate(TimeProvider timeProvider)
        {
            _timeProvider = timeProvider;
        }

        /// <summary>
        /// Blocks until it is this caller's turn to send, given <paramref name="minInterval"/>
        /// since the previous reserved send.
        /// </summary>
        public async Task WaitForTurnAsync(TimeSpan minInterval, CancellationToken ct)
        {
            var delay = await ReserveSlotAsync(minInterval, ct);
            if (delay > TimeSpan.Zero)
            {
                await Task.Delay(delay, _timeProvider, ct);
            }
        }

        private async Task<TimeSpan> ReserveSlotAsync(TimeSpan minInterval, CancellationToken ct)
        {
            await _reservationLock.WaitAsync(ct);
            try
            {
                var now = _timeProvider.GetUtcNow();
                var earliestSlot = _nextAvailableSlotUtc is { } next && next > now ? next : now;

                _nextAvailableSlotUtc = earliestSlot + minInterval;

                return earliestSlot - now;
            }
            finally
            {
                _reservationLock.Release();
            }
        }
    }
}
