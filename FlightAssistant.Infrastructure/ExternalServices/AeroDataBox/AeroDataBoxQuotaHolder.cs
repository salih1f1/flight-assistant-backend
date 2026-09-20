using FlightAssistant.Application.Interfaces.Providers;

namespace FlightAssistant.Infrastructure.ExternalServices.AeroDataBox
{
    /// <summary>
    /// Holds the last quota reading. A singleton with an atomic reference write, the same shape
    /// <c>FlightSyncHeartbeat</c> uses: a reader can never see a torn value, and there is no lock on
    /// the read path.
    /// </summary>
    public sealed class AeroDataBoxQuotaHolder : IProviderQuotaReader
    {
        private ProviderQuota? _current;

        public ProviderQuota? AeroDataBox => Volatile.Read(ref _current);

        /// <summary>Records a reading. A response missing either header changes nothing — the last
        /// known value is better than discarding it.</summary>
        public void Record(int? remaining, int? limit, DateTimeOffset now)
        {
            if (remaining is null || limit is null) return;
            Volatile.Write(ref _current, new ProviderQuota(remaining.Value, limit.Value, now));
        }
    }
}
