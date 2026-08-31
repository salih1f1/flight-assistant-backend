using FlightAssistant.Domain.Configuration.Entities;
using FlightAssistant.Infrastructure.Persistance;

namespace FlightAssistant.Infrastructure.ExternalServices.AeroApi
{
    /// <summary>
    /// Writes a metered call to <c>aeroapi_calls</c>. Scoped, like every other consumer of
    /// <see cref="FlightAssistantDbContext"/> — <see cref="AeroApiUsageRecordingHandler"/> creates
    /// its own scope per row because an <c>HttpMessageHandler</c> outlives any request scope.
    /// </summary>
    public sealed class DbAeroApiUsageRecorder : IAeroApiUsageRecorder
    {
        private readonly FlightAssistantDbContext _db;
        public DbAeroApiUsageRecorder(FlightAssistantDbContext db) => _db = db;

        public async Task RecordAsync(AeroApiCall call, CancellationToken ct)
        {
            _db.AeroApiCalls.Add(call);
            await _db.SaveChangesAsync(ct);
        }
    }
}
