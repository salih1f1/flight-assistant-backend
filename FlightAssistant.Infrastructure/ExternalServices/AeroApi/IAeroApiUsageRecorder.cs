using FlightAssistant.Domain.Configuration.Entities;

namespace FlightAssistant.Infrastructure.ExternalServices.AeroApi
{
    /// <summary>
    /// Persists one metered AeroAPI call. Deliberately a seam rather than a direct
    /// <c>FlightAssistantDbContext</c> dependency inside
    /// <see cref="AeroApiUsageRecordingHandler"/>: the handler lives in the outbound HTTP pipeline,
    /// which is exercised end to end by <c>AeroApiClientPipelineTests</c> against a container that
    /// has no database, and a write that could only ever throw there would make "was this call
    /// metered?" unobservable.
    ///
    /// <para>
    /// Implementations are honest about failure — they throw. Swallowing belongs in the handler,
    /// which is the thing that must never fail a flight request.
    /// </para>
    /// </summary>
    public interface IAeroApiUsageRecorder
    {
        Task RecordAsync(AeroApiCall call, CancellationToken ct);
    }
}
